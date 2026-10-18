#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
push-history-via-api.py — 用 GitHub Git Data API 把一段本地提交链**保真**推到远端分支。

与 push-via-api.py 的区别
-------------------------
push-via-api.py 把本地 HEAD 压成远端 tip 之上的**一个**提交（适合单次交付）。
本脚本保留链条上每一个提交：逐个创建 tree/commit，父提交指针逐级接续，
因此远端得到的提交 SHA 与本地逐一相同——分叉历史、fast-forward、后续
`git fetch upstream` 都不被破坏。用于把"导入的上游历史"推到自己的 fork。

安全性
------
- 每个创建的 tree/commit 的 SHA 都与本地对应对象校验，任何不符即中止；
- 只接受 fast-forward（force=False），远端 tip 必须能在本地历史中找到等价基线；
- blob 按内容寻址，POST 是幂等的；已存在于远端的 blob 会先跳过；
- 支持合并提交：差量相对第一父计算，parents 按本地原样透传，tree 以第一父的
  远端 tree 为 base_tree 增量构造。

用法::

    GITHUB_TOKEN=... python eng/release/push-history-via-api.py \\
        --repo dongfangzhizhu/audio.cpp --dir D:/SouceCode/python2net/audio/audio.cpp

    # 只演练不写远端
    ... --dry-run
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures as cf
import json
import os
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = "https://api.github.com"
UA = "audiocpp-dotnet-pushhistory"
DRY = False


def info(m: str) -> None:
    print(f":: {m}", flush=True)


def die(m: str, code: int = 1) -> None:
    print(f"!!! {m}", file=sys.stderr, flush=True)
    sys.exit(code)


def git(repo: pathlib.Path, *args: str) -> str:
    # 显式 UTF-8：Windows 默认 text=True 走本地代码页（cp936），提交信息含非 ASCII 时
    # 会静默乱码 → 远端 commit SHA 与本地不符。
    p = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, encoding="utf-8")
    if p.returncode != 0:
        die(f"git {' '.join(args)} 失败: {p.stderr.strip()[:400]}")
    return p.stdout


def git_ok(repo: pathlib.Path, *args: str) -> bool:
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True).returncode == 0


class Gh:
    def __init__(self, token: str):
        self.token = token
        self.calls = 0

    def call(self, method: str, path: str, body=None, retries: int = 8):
        url = path if path.startswith("http") else API + path
        data = json.dumps(body).encode() if body is not None else None
        last = None
        for attempt in range(retries):
            self.calls += 1
            req = urllib.request.Request(url, data=data, method=method, headers={
                "Authorization": f"Bearer {self.token}",
                "User-Agent": UA,
                "Accept": "application/vnd.github+json",
                **({"Content-Type": "application/json"} if data else {}),
            })
            try:
                with urllib.request.urlopen(req, timeout=300) as r:
                    raw = r.read()
                    return r.status, (json.loads(raw) if raw else None)
            except urllib.error.HTTPError as e:
                raw = e.read()
                try:
                    payload = json.loads(raw)
                except Exception:
                    payload = None
                if e.code in (403, 429, 500, 502, 503, 504) and attempt < retries - 1:
                    reset = e.headers.get("X-RateLimit-Reset")
                    wait = 10 * (attempt + 1)
                    if reset:
                        wait = max(wait, min(120, int(reset) - int(time.time()) + 2))
                    info(f"  http {e.code}，{wait}s 后重试")
                    time.sleep(wait)
                    continue
                return e.code, payload
            except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as e:
                last = e
                if attempt < retries - 1:
                    wait = 5 * (attempt + 1)
                    info(f"  网络错误 ({type(e).__name__})，{wait}s 后重试")
                    time.sleep(wait)
                    continue
                die(f"请求 {method} {path} 失败: {last}")
        die(f"请求 {method} {path} 重试次数用尽")


def tree_entries(repo: pathlib.Path, sha: str) -> dict[str, tuple[str, str]]:
    """{path: (mode, blob_or_gitlink_sha)}，与 git ls-tree -r 等价。"""
    out: dict[str, tuple[str, str]] = {}
    raw = subprocess.run(["git", "-C", str(repo), "ls-tree", "-r", "-z", sha],
                         capture_output=True, encoding="utf-8").stdout
    for rec in raw.split("\0"):
        if not rec:
            continue
        meta, _, path = rec.partition("\t")
        mode, _typ, osha = meta.split()
        out[path] = (mode, osha)
    return out


def commit_meta(repo: pathlib.Path, sha: str) -> dict:
    raw = git(repo, "log", "-1",
              "--format=%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%H%x00%B", sha).split("\0")
    # GitHub 的 POST /git/commits 对 message **原样存储、不补尾随换行**，而 git 对象
    # 的消息以 \n 结尾。%B 输出 = body（含其尾 \n）+ log 记录终止 \n，因此只能去掉
    # 终止符那一个 \n、保留 body 原样；早先的 rstrip("\n") 把 body 的尾 \n 也删了，
    # 远端 commit 与本地 SHA 必不符（实测：本地 2dad606a vs 远端 7d4108aa，差异正是
    # 消息末尾一个换行——本地对象去掉尾 \n 重算即得远端 SHA）。
    msg = raw[7]
    if msg.endswith("\n"):
        msg = msg[:-1]
    return {
        "author": {"name": raw[0], "email": raw[1], "date": raw[2]},
        "committer": {"name": raw[3], "email": raw[4], "date": raw[5]},
        "sha": raw[6],
        "message": msg,
    }


def main() -> int:
    global DRY
    ap = argparse.ArgumentParser(description="保真推送本地提交链到远端分支（Git Data API）")
    ap.add_argument("--repo", required=True, help="owner/name")
    ap.add_argument("--dir", required=True, help="本地 git 仓库目录")
    ap.add_argument("--branch", default="main")
    ap.add_argument("--to", default="HEAD", help="本地目标提交（默认 HEAD）")
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    DRY = args.dry_run

    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if not token:
        die("请设置 GITHUB_TOKEN 或 GH_TOKEN")
    gh = Gh(token)
    repo = pathlib.Path(args.dir).resolve()
    owner, name = args.repo.split("/", 1)
    local_head = git(repo, "rev-parse", args.to).strip()
    info(f"本地目标 = {local_head[:12]}")

    # 1) 远端 tip
    st, ref = gh.call("GET", f"/repos/{owner}/{name}/git/ref/heads/{args.branch}")
    if st != 200:
        die(f"取远端分支失败 HTTP {st}: {ref}")
    remote_sha = ref["object"]["sha"]
    info(f"远端 {args.branch} = {remote_sha[:12]}")
    if remote_sha == local_head:
        info("两边已经一致，无需推送")
        return 0

    st, rcommit = gh.call("GET", f"/repos/{owner}/{name}/git/commits/{remote_sha}")
    if st != 200:
        die(f"取远端提交失败 HTTP {st}: {rcommit}")
    remote_tree = rcommit["tree"]["sha"]

    # 2) 基线：远端 tip 本身在本地 → 真 fast-forward；否则按树找等价本地提交
    base = remote_sha
    if not git_ok(repo, "cat-file", "-e", remote_sha):
        base = None
        for sha in git(repo, "rev-list", "--max-count=500", local_head).split():
            if git(repo, "rev-parse", f"{sha}^{{tree}}").strip() == remote_tree:
                base = sha
                info(f"远端 tip 内容等于本地 {sha[:12]}（SHA 不同，早前 API 推送造成）")
                break
        if base is None:
            die("远端 tip 既不是本地祖先，内容也不在本地历史中；请先处理分叉")
    else:
        if not git_ok(repo, "merge-base", "--is-ancestor", remote_sha, local_head):
            die(f"远端 tip {remote_sha[:12]} 不是本地目标的祖先，拒绝非 fast-forward 推送")
        info("远端 tip 是本地祖先，正常前进")

    commits = git(repo, "rev-list", "--reverse", "--topo-order", f"{base}..{local_head}").split()
    info(f"待推送提交：{len(commits)} 个")

    # 3) 远端已有的 blob（tip 的递归树），这些不必再 POST
    known_blobs: set[str] = set()
    st, rtree = gh.call("GET", f"/repos/{owner}/{name}/git/trees/{remote_tree}?recursive=1")
    if st == 200 and not rtree.get("truncated"):
        known_blobs = {e["sha"] for e in rtree["tree"] if e["type"] == "blob"}
        info(f"远端已有 blob：{len(known_blobs)}")
    else:
        info("远端树不可用或过大，blob 将按需上传（POST 幂等）")

    # 4) 逐提交算差量——注意两个易错点：
    #    a) 差量基线必须是该提交的**第一父**（合并提交的结果树相对第一父），
    #       而不是 rev-list 顺序里的上一条；
    #    b) parents 必须按本地原样透传（合并提交有双亲，SHA 依赖全部父指针）。
    def parents_of(sha: str) -> list[str]:
        return git(repo, "log", "-1", "--format=%P", sha).split()

    plans = []
    need_blobs: dict[str, None] = {}
    for csha in commits:
        ps = parents_of(csha)
        if not ps:
            die(f"{csha[:12]} 没有父提交（根提交不支持）")
        base_entries = tree_entries(repo, ps[0])
        cur = tree_entries(repo, csha)
        changed = []  # (path, mode, sha or None)
        for path, (mode, osha) in cur.items():
            if base_entries.get(path) != (mode, osha):
                changed.append((path, mode, osha))
                if mode != "160000" and osha not in known_blobs:
                    need_blobs[osha] = None
        for path, (pmode, _psha) in base_entries.items():
            if path not in cur:
                changed.append((path, pmode, None))
        plans.append({"sha": csha, "tree": git(repo, "rev-parse", f"{csha}^{{tree}}").strip(),
                      "parents": ps, "changed": changed})
    info(f"需要上传的 blob：{len(need_blobs)}")

    # 5) 并发上传 blob（内容寻址，幂等；返回 sha 必须与本地一致）
    todo = [s for s in need_blobs if s not in known_blobs]

    def upload(sha: str) -> str:
        content = subprocess.run(["git", "-C", str(repo), "cat-file", "blob", sha],
                                 capture_output=True).stdout
        if DRY:
            return sha
        st, blob = gh.call("POST", f"/repos/{owner}/{name}/git/blobs",
                           {"content": base64.b64encode(content).decode(), "encoding": "base64"})
        if st not in (200, 201):
            die(f"上传 blob {sha[:12]} 失败 HTTP {st}: {blob}")
        if blob.get("sha") != sha:
            die(f"blob SHA 不符：本地 {sha[:12]}，远端 {blob.get('sha')}")
        return sha

    if todo:
        done = 0
        with cf.ThreadPoolExecutor(max_workers=args.workers) as ex:
            futs = [ex.submit(upload, s) for s in todo]
            for fut in cf.as_completed(futs):
                fut.result()
                done += 1
                if done % 200 == 0:
                    info(f"  已上传 {done}/{len(todo)}")
        info(f"blob 上传完成：{len(todo)}")

    # 6) 逐提交建 tree → 建 commit → 接父链
    #    每个提交的 tree 以其第一父在远端的 tree 为 base_tree；merge 提交的双亲
    #    此时必须都已在远端（topo-order 保证父母先入队；不在范围内的父必可达 base）。
    created_trees: dict[str, str] = {remote_sha: remote_tree}
    for i, plan in enumerate(plans):
        entries = []
        for (path, mode, osha) in plan["changed"]:
            entry = {"path": path, "mode": mode, "sha": osha}
            entry["type"] = "commit" if mode == "160000" else "blob"
            entries.append(entry)
        for p in plan["parents"]:
            if p not in created_trees:
                die(f"{plan['sha'][:9]} 的父提交 {p[:9]} 尚未推送到远端，顺序错误")
        base_tree = created_trees[plan["parents"][0]]
        if DRY:
            info(f"DRY-RUN {plan['sha'][:9]}: parents={len(plan['parents'])} "
                 f"{len(entries)} 个变更条目")
            created_trees[plan["sha"]] = plan["tree"]
            continue
        st, tree = gh.call("POST", f"/repos/{owner}/{name}/git/trees",
                           {"base_tree": base_tree, "tree": entries})
        if st not in (200, 201):
            die(f"建 tree 失败（{plan['sha'][:9]}）HTTP {st}: {tree}")
        if tree["sha"] != plan["tree"]:
            die(f"tree SHA 不符（{plan['sha'][:9]}）：本地 {plan['tree'][:12]}，远端 {tree['sha'][:12]}")
        meta = commit_meta(repo, plan["sha"])
        st, commit = gh.call("POST", f"/repos/{owner}/{name}/git/commits", {
            "message": meta["message"],
            "tree": tree["sha"],
            "parents": plan["parents"],
            "author": meta["author"],
            "committer": meta["committer"],
        })
        if st not in (200, 201):
            die(f"建 commit 失败（{plan['sha'][:9]}）HTTP {st}: {commit}")
        if commit["sha"] != plan["sha"]:
            die(f"commit SHA 不符：本地 {plan['sha'][:12]}，远端 {commit['sha'][:12]}")
        created_trees[plan["sha"]] = tree["sha"]
        if (i + 1) % 25 == 0:
            info(f"  已推送 {i + 1}/{len(plans)}")

    if DRY:
        info("DRY-RUN 结束，未写远端")
        return 0

    # 7) 快进推进分支
    tip = plans[-1]["sha"]
    if tip not in created_trees:
        die("内部错误：最后一个提交未记录")
    st, upd = gh.call("PATCH", f"/repos/{owner}/{name}/git/refs/heads/{args.branch}",
                      {"sha": tip, "force": False})
    if st != 200:
        die(f"推进分支失败 HTTP {st}: {upd}")
    info(f"已推进 {args.branch} -> {tip[:12]}（{len(plans)} 个提交，SHA 与本地一致）")
    info(f"API 调用合计：{gh.calls}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
