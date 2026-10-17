# -*- coding: utf-8 -*-
"""
import-upstream-history.py — 在 git fetch 不可达（github.com:443 被拦）的环境下，
通过 GitHub Git Data API 把上游提交 DAG 逐条重放进本地 audio.cpp 仓库。

与简单线性重放的区别：范围内可能含合并提交（第二父提交在范围外），因此从 head 递归
收集所有本地缺失的提交，拓扑排序后按原 parents 重建。内容寻址保证：每个 commit/tree
SHA 必须与上游一致，任何一条不符即中止——宁可失败也不留下"看起来像"的历史。

提交对象的字节级重建（本脚本的核心难点，2026-10-16 实测定型）
------------------------------------------------------------
GitHub API 的日期字段一律归一化成 UTC（Z），而 commit 对象里存的是**原始时区偏移**
（如 -0400）——直接拿 API 元数据 commit-tree 必然 SHA 不符。两条重建路径：

1. 签名提交（verification.reason=valid，占多数）：verification.payload 就是
   commit 对象去掉 gpgsig 头后的**原始字节**，signature 是完整 armored PGP 块。
   把签名按 git 的折叠规则（首行 'gpgsig ' 起、后续每行前置一个空格、空行保留为
   空格行）插回 committer 行之后即可——已用本地已知对象逐字节验证通过。
2. 未签名提交（reason=unsigned，上游作者本机直推）：API 只给 UTC 时刻，时区丢失。
   但对象格式完全已知、唯一未知量是时区偏移字符串 ⇒ 在 Python 里枚举候选偏移，
   用 SHA-1 反算校验。命中即字节级铁证（哈希碰撞概率可忽略），不命中继续找。
   这是**自验证搜索**，不是猜测：产物要么与上游 SHA 逐一相符，要么脚本中止。

其他踩坑（均已修复，勿回退）：
- hash-object 必须 --no-filters：Windows core.autocrlf=true 会把含 CRLF 的 blob
  清成 LF，算出另一个 SHA（实测 expected 9d05ae46 / got 8df2e021）。
- 7119 次顺序 git cat-file -e 探测约 19 分钟 → missing_objects 单次 --batch-check。
- 150 棵递归 tree 顺序拉取约 20 分钟 → 并发 + 落盘缓存。
- 已下载的 blob 重跑时跳过重下；commit 元数据同样落盘缓存。

用法：
    python import-upstream-history.py --repo D:/SouceCode/python2net/audio/audio.cpp \
        --to 22cad6af5043a2d7c7f1d9a0e6c9d7996a80dfc8 [--tags v0.9.0 v0.9.1 v0.9.2]

令牌从 Git Credential Manager 取（git credential fill），不落盘。
"""
import argparse
import concurrent.futures as cf
import datetime as dt
import hashlib
import http.client
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

API = "https://api.github.com"
UPSTREAM = "0xShug0/audio.cpp"

# git 对象里真实出现过的 UTC 偏移（±HHMM）。未签名提交的时区搜索空间。
OFFSETS = [
    "-1200", "-1100", "-1030", "-1000", "-0930", "-0900", "-0800", "-0700",
    "-0600", "-0500", "-0430", "-0400", "-0330", "-0300", "-0200", "-0100",
    "+0000", "+0100", "+0200", "+0300", "+0330", "+0400", "+0430", "+0500",
    "+0530", "+0545", "+0600", "+0630", "+0700", "+0800", "+0845", "+0900",
    "+0930", "+1000", "+1030", "+1100", "+1200", "+1245", "+1300", "+1400",
]


def log(msg):
    print(msg, flush=True)


def get_token(repo):
    p = subprocess.run(["git", "credential", "fill"],
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True, timeout=30, cwd=repo)
    if p.returncode != 0:
        raise SystemExit("git credential fill failed")
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    raise SystemExit("no credential for github.com in GCM")


class Gh:
    def __init__(self, token):
        self.token = token
        self.calls = 0

    def get(self, path, accept="application/vnd.github+json", retries=8):
        url = path if path.startswith("http") else f"{API}{path}"
        last = None
        for attempt in range(retries):
            self.calls += 1
            req = urllib.request.Request(url, headers={
                "User-Agent": "audiocpp-upstream-import",
                "Accept": accept,
                "Authorization": f"Bearer {self.token}",
            })
            try:
                with urllib.request.urlopen(req, timeout=180) as r:
                    return r.read()
            except urllib.error.HTTPError as e:
                # 403/429 可能是限流，也可能是连接被重置后的假象；统一退避重试
                if e.code in (403, 429, 500, 502, 503, 504) and attempt < retries - 1:
                    reset = e.headers.get("X-RateLimit-Reset")
                    wait = 10 * (attempt + 1)
                    if reset:
                        wait = max(wait, min(120, int(reset) - int(time.time()) + 2))
                    log(f"  http {e.code} on {path[:60]}, sleep {wait}s")
                    time.sleep(wait)
                    continue
                raise
            except (urllib.error.URLError, http.client.IncompleteRead, TimeoutError,
                    ConnectionError, http.client.HTTPException) as e:
                # 连接层抖动（实测遇到过 tree 下载中途 IncompleteRead）
                last = e
                if attempt < retries - 1:
                    wait = 5 * (attempt + 1)
                    log(f"  network error ({type(e).__name__}) on {path[:60]}, retry in {wait}s")
                    time.sleep(wait)
                    continue
                raise
        raise RuntimeError(f"GET {path} failed: {last}")

    def json(self, path):
        return json.loads(self.get(path).decode("utf-8"))


def git(repo, args, env=None, input_bytes=None, check=True):
    e = dict(os.environ)
    if env:
        e.update(env)
    p = subprocess.run(["git"] + args, cwd=repo, capture_output=True, input=input_bytes, env=e)
    if check and p.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {p.stderr.decode('utf-8', 'replace')}")
    return p


def object_exists(repo, sha):
    return git(repo, ["cat-file", "-e", sha], check=False).returncode == 0


def missing_objects(repo, shas):
    """批量探测：一次 git cat-file --batch-check 进程代替 N 次 cat-file -e。

    Windows 上每次 git 子进程约 125-160ms，7119 次顺序探测要 15-20 分钟；
    batch-check 从 stdin 读 SHA 列表、一次输出 "<sha> <type> <size>" 或
    "<sha> missing"，全部压缩到一个进程里（实测 <10s）。
    """
    uniq = sorted(set(shas))
    if not uniq:
        return set()
    p = git(repo, ["cat-file", "--batch-check", "--buffer"],
            input_bytes=("\n".join(uniq) + "\n").encode("ascii"), check=False)
    if p.returncode != 0:
        # 退化：逐個探测（极老版本 git 没有 --buffer）
        return {s for s in uniq if not object_exists(repo, s)}
    missing = set()
    for line in p.stdout.decode("utf-8", "replace").splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[-1] == "missing":
            missing.add(parts[0])
    return missing


def collect_missing(repo, gh, head, cache_path=None):
    """从 head 递归收集本地缺失的提交（含合并的第二父链）。

    走 git/commits 端点（扁平结构 + verification），而不是 REST /commits——
    后者把 author/committer 嵌在 commit 子对象里且同样丢时区，我们需要的
    payload/signature 只有 Git Database 端点给。结果落盘缓存，重跑跳过 API。
    """
    if cache_path and os.path.exists(cache_path):
        try:
            with open(cache_path, "r", encoding="utf-8") as f:
                cached = json.load(f)
            if head in cached:
                log(f"missing commits: {len(cached)} (from cache)")
                return cached
        except Exception as e:
            log(f"  commit cache unreadable ({e}), refetching")

    missing = {}
    queue = [head]
    while queue:
        sha = queue.pop()
        if sha in missing or object_exists(repo, sha):
            continue
        c = gh.json(f"/repos/{UPSTREAM}/git/commits/{sha}")
        v = c.get("verification") or {}
        parents = [p["sha"] for p in c["parents"]]
        missing[sha] = {
            "sha": c["sha"],
            "tree": c["tree"]["sha"],
            "parents": parents,
            "author": c["author"],       # {name, email, date(ISO, 恒为 Z)}
            "committer": c["committer"],
            "message": c["message"],
            "payload": v.get("payload"),     # 签名提交的对象原文（不含 gpgsig）
            "signature": v.get("signature"), # armored PGP 块
            "reason": v.get("reason"),
        }
        queue.extend(parents)
    if cache_path:
        with open(cache_path, "w", encoding="utf-8") as f:
            json.dump(missing, f)
    return missing


def topo_order(missing):
    """父母先于子女（Kahn；父母已在本地库的视为已满足）。"""
    indeg = {}
    children = {}
    for sha, c in missing.items():
        deps = [p for p in c["parents"] if p in missing]
        indeg[sha] = len(deps)
        for p in deps:
            children.setdefault(p, []).append(sha)
    ready = [s for s, d in indeg.items() if d == 0]
    order = []
    while ready:
        s = ready.pop()
        order.append(s)
        for ch in children.get(s, []):
            indeg[ch] -= 1
            if indeg[ch] == 0:
                ready.append(ch)
    if len(order) != len(missing):
        raise SystemExit("topological sort failed (cycle?)")
    return order


def git_sha1(obj_type, content):
    """git 对象 SHA：sha1("<type> <len>\\0" + content)。"""
    h = hashlib.sha1()
    h.update(f"{obj_type} {len(content)}\0".encode("ascii"))
    h.update(content)
    return h.hexdigest()


def iso_epoch(iso):
    t = dt.datetime.strptime(iso, "%Y-%m-%dT%H:%M:%S%z")
    return int(t.timestamp())


def fold_gpgsig(signature):
    """把 armored PGP 块折叠成 git 对象的 gpgsig 头。

    规则（用本地已知对象逐字节验证过）：首行 'gpgsig ' 开头；后续每一行前置
    一个空格——**包括空行**（armor BEGIN 后的空行变成 ' '，尾部换行产生的
    空行同样保留，否则对象差 2-4 字节）。
    """
    lines = signature.split("\n")  # signature 以 \n 结尾，末元素是 ''，必须保留
    return ("gpgsig " + lines[0] + "\n"
            + "".join(" " + l + "\n" for l in lines[1:]))


def build_commit_from_payload(meta):
    """签名提交：payload（对象去 gpgsig 后的原字节）+ 折叠签名头。"""
    payload = meta["payload"].encode("utf-8")
    if not meta.get("signature"):
        return payload
    idx = payload.find(b"\n\n")
    if idx < 0:
        head, msg = payload, b""
    else:
        head, msg = payload[:idx], payload[idx + 2:]
    return head + b"\n" + fold_gpgsig(meta["signature"]).encode("utf-8") + b"\n" + msg


def build_commit_unsigned(meta, expected_sha):
    """未签名提交：API 元数据 + 时区偏移枚举，SHA-1 反算自验证。

    API 的 author/committer.date 恒为 Z（UTC 时刻），对象里却是原始偏移。
    时刻已知、格式已知 ⇒ 唯一自由参数是偏移字符串。同一偏移优先（本机直推的
    常见形态），再试全组合；message 末尾换行也作为一个维度（git commit 习惯
    带尾换行，GitHub 页面创建的提交不带——两种都见过）。
    """
    a, cm = meta["author"], meta["committer"]
    a_epoch, cm_epoch = iso_epoch(a["date"]), iso_epoch(cm["date"])
    parent_lines = "".join(f"parent {p}\n" for p in meta["parents"])
    messages = [meta["message"]]
    if not meta["message"].endswith("\n"):
        messages.append(meta["message"] + "\n")

    def make(off_a, off_c, msg):
        return (f"tree {meta['tree']}\n{parent_lines}"
                f"author {a['name']} <{a['email']}> {a_epoch} {off_a}\n"
                f"committer {cm['name']} <{cm['email']}> {cm_epoch} {off_c}\n\n"
                f"{msg}").encode("utf-8")

    for msg in messages:
        for off in OFFSETS:  # 同一偏移（最常见）
            obj = make(off, off, msg)
            if git_sha1("commit", obj) == expected_sha:
                return obj, (off, off)
        for off_a in OFFSETS:  # 作者/提交者偏移不同（跨 DST 边界等）
            for off_c in OFFSETS:
                if off_a == off_c:
                    continue
                obj = make(off_a, off_c, msg)
                if git_sha1("commit", obj) == expected_sha:
                    return obj, (off_a, off_c)
    return None, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--to", dest="head", required=True)
    ap.add_argument("--tags", nargs="*", default=[])
    ap.add_argument("--workdir", default=None)
    args = ap.parse_args()
    repo = os.path.abspath(args.repo)
    workdir = args.workdir or tempfile.mkdtemp(prefix="upstream-blobs-")
    os.makedirs(workdir, exist_ok=True)

    gh = Gh(get_token(repo))

    missing = collect_missing(repo, gh, args.head,
                              cache_path=os.path.join(workdir, "commitmeta-cache.json"))
    order = topo_order(missing)
    log(f"missing commits: {len(missing)} (topological order built)")

    # 全量 tree → 差集出本地没有的 blob SHA
    # 踩坑记录：① tree JSON 顺序拉取 150 次要 20+ 分钟 → 改并发；
    #          ② 探测用 object_exists 逐个 cat-file -e（7119 次子进程 ≈ 19 分钟）
    #             → 改 missing_objects 批量 batch-check；
    #          ③ tree 结果落盘缓存，中断重跑不必再求 API。
    needed = set()
    trees = {}
    cache_path = os.path.join(workdir, "trees-cache.json")
    cached = {}
    if os.path.exists(cache_path):
        try:
            with open(cache_path, "r", encoding="utf-8") as f:
                cached = json.load(f)
        except Exception as e:
            log(f"  tree cache unreadable ({e}), refetching")
            cached = {}

    def fetch_tree(sha):
        t = gh.json(f"/repos/{UPSTREAM}/git/trees/{missing[sha]['tree']}?recursive=1")
        if t.get("truncated"):
            raise SystemExit(f"tree truncated at {sha[:9]}")
        entries = []
        for e in t["tree"]:
            if e["type"] == "blob":
                entries.append((e["mode"], e["sha"], e["path"]))
            elif e["type"] == "commit":
                entries.append(("160000", e["sha"], e["path"]))
        return sha, entries

    todo_trees = [s for s in order if str(s) not in cached]
    for s in cached:
        trees[s] = [tuple(x) for x in cached[s]]
    if todo_trees:
        log(f"  fetching {len(todo_trees)} trees (cached: {len(cached)})")
        with cf.ThreadPoolExecutor(max_workers=6) as ex:
            for sha, entries in ex.map(fetch_tree, todo_trees):
                trees[sha] = entries
                cached[sha] = entries
        with open(cache_path, "w", encoding="utf-8") as f:
            json.dump(cached, f)
    for sha in order:
        for (_mode, bsha, _path) in trees[sha]:
            if _mode != "160000":
                needed.add(bsha)
    log(f"distinct blob shas across all trees: {len(needed)}")

    todo = sorted(missing_objects(repo, needed))
    log(f"blobs to fetch: {len(todo)} (already present: {len(needed) - len(todo)})")

    def fetch_one(sha):
        path = os.path.join(workdir, sha)
        if os.path.exists(path) and os.path.getsize(path) > 0:
            return sha, os.path.getsize(path)  # 上次中断已下载，跳过重下
        raw = gh.get(f"/repos/{UPSTREAM}/git/blobs/{sha}", accept="application/vnd.github.raw")
        with open(path, "wb") as f:
            f.write(raw)
        return sha, len(raw)

    done = 0
    if todo:
        with cf.ThreadPoolExecutor(max_workers=6) as ex:
            futs = [ex.submit(fetch_one, s) for s in todo]
            for fut in cf.as_completed(futs):
                fut.result()
                done += 1
                if done % 200 == 0:
                    log(f"  fetched {done}/{len(todo)}")
    log(f"fetched all {len(todo)} blobs")

    # 批量 hash-object -w 并核对 SHA
    # 必须 --no-filters：Windows 上 core.autocrlf=true 会把 CRLF 清成 LF，
    # 上游含 CRLF 的 blob 会被改写成本地另一个 SHA（实测踩坑：expected
    # 9d05ae46, got 8df2e021），整个重放的"内容寻址保真"前提即失效。
    paths = [os.path.join(workdir, s) for s in todo]
    for i in range(0, len(paths), 400):
        batch_paths = paths[i:i + 400]
        batch_shas = todo[i:i + 400]
        p = git(repo, ["hash-object", "-w", "--no-filters", "--stdin-paths"],
                input_bytes=("\n".join(batch_paths) + "\n").encode("utf-8"))
        out = p.stdout.decode().split()
        if len(out) != len(batch_paths):
            raise SystemExit(f"hash-object batch mismatch: {len(out)} vs {len(batch_paths)}")
        for sha, got in zip(batch_shas, out):
            if sha != got:
                raise SystemExit(f"blob sha mismatch: expected {sha}, got {got}")
    log("blobs written into object store")

    # 阶段一：tree 对象（update-index + write-tree，逐 SHA 校验）
    # 两个本机环境约束：
    # ① index 文件不能 os.remove——沙箱对删除有配额（单轮 >50 次直接杀进程）；
    # ② 零字节文件也不是合法索引（git 要求 DIRC 头 + 尾校验和）——
    # 每轮用 `git read-tree --empty` 把同一个 index 文件重置成合法空索引。
    env_common = {}
    index_file = os.path.join(workdir, "index.tmp")
    for idx, sha in enumerate(order):
        entries = trees[sha]
        git(repo, ["read-tree", "--empty"],
            env={**env_common, "GIT_INDEX_FILE": index_file})
        lines = [f"{mode} {bsha}\t{path}" for (mode, bsha, path) in entries]
        git(repo, ["update-index", "--index-info"],
            env={**env_common, "GIT_INDEX_FILE": index_file},
            input_bytes=("\n".join(lines) + "\n").encode("utf-8"))
        tree_sha = git(repo, ["write-tree"],
                       env={**env_common, "GIT_INDEX_FILE": index_file}).stdout.decode().strip()
        if tree_sha != missing[sha]["tree"]:
            raise SystemExit(f"tree mismatch at {sha[:9]}: built {tree_sha}, upstream {missing[sha]['tree']}")
        if (idx + 1) % 25 == 0:
            log(f"  trees {idx + 1}/{len(order)}")
    log("trees written into object store")

    # 阶段二：commit 对象（payload 复原 / 时区枚举，逐 SHA 校验）
    n_signed = n_searched = 0
    for idx, sha in enumerate(order):
        c = missing[sha]
        if c.get("payload"):
            obj = build_commit_from_payload(c)
            n_signed += 1
        else:
            obj, offs = build_commit_unsigned(c, sha)
            if obj is None:
                raise SystemExit(
                    f"unsigned commit {sha[:9]}: 时区枚举未命中（message 字节可能也被 API 改动）\n"
                    f"  author={c['author']}\n  committer={c['committer']}\n"
                    f"  msg={c['message'][:200]!r}")
            n_searched += 1
            if offs[0] != "+0000" or offs[1] != "+0000":
                log(f"  {sha[:9]}: unsigned, offsets author={offs[0]} committer={offs[1]}")
        if git_sha1("commit", obj) != sha:
            raise SystemExit(f"commit object sha mismatch at {sha[:9]}")
        p = git(repo, ["hash-object", "-w", "-t", "commit", "--stdin"], input_bytes=obj)
        got = p.stdout.decode().strip()
        if got != sha:
            raise SystemExit(f"hash-object mismatch at {sha[:9]}: built {got}, upstream {sha}")
        if (idx + 1) % 25 == 0:
            log(f"  commits {idx + 1}/{len(order)}")
    log(f"commits written into object store (signed/payload: {n_signed}, offset-searched: {n_searched})")

    log(f"replay complete: {len(order)} commits, final {args.head}, api calls: {gh.calls}")

    # 轻量 tag（describe 用）
    for tag in args.tags:
        ref = gh.json(f"/repos/{UPSTREAM}/git/refs/tags/{tag}")
        obj_sha, obj_type = ref["object"]["sha"], ref["object"]["type"]
        if obj_type == "tag":
            obj_sha = gh.json(f"/repos/{UPSTREAM}/git/tags/{obj_sha}")["object"]["sha"]
        if not object_exists(repo, obj_sha):
            log(f"  tag {tag} -> {obj_sha[:9]} not replayed, skip")
            continue
        git(repo, ["tag", "-f", tag, obj_sha], check=False)
        log(f"  tag {tag} -> {obj_sha[:9]}")

    git(repo, ["update-ref", "refs/remotes/origin/main", args.head])
    log("refs/remotes/origin/main -> " + args.head)


if __name__ == "__main__":
    main()
