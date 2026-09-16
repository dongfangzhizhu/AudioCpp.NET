#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""当 `git push` 走不通时，用 GitHub Git Data API 把本地提交搬到远端分支。

为什么需要
----------
本机出网要过一个代理，实测 ``api.github.com`` / ``codeload.github.com`` /
``objects.githubusercontent.com`` 都通，但 ``github.com:443`` 会直接超时：

    fatal: unable to access 'https://github.com/.../': Failed to connect to
    github.com:443 after 21085 ms: Could not connect to server

`git push` 正是死在最后那条连接上。而 GitHub 的 Git Data API 全部挂在
``api.github.com`` 之下，所以可以绕过去。

做法
----
对比本地 HEAD 的树与远端分支的树，只上传有差异的 blob，然后建 tree、建 commit、
推进分支引用——等价于一次 fast-forward push，产生的是正常的提交（作者/提交者/
时间戳都从本地提交继承），不是伪造的。

用法::

    GITHUB_TOKEN=... python eng/release/push-via-api.py \\
        --repo dongfangzhizhu/AudioCpp.NET --branch main

    # 只看会动哪些文件，不写远端
    ... --dry-run

令牌读取方式与 publish-github.py 一致：环境变量或 --token-file，不接受命令行传参。
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import pathlib
import subprocess
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"
UA = "audiocpp-dotnet-pushapi"

DRY = False


def info(m: str) -> None:
    print(f":: {m}", flush=True)


def warn(m: str) -> None:
    print(f"!! {m}", flush=True)


def die(m: str, code: int = 1) -> None:
    print(f"!!! {m}", file=sys.stderr, flush=True)
    sys.exit(code)


def git(repo: pathlib.Path, *args: str) -> str:
    p = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    if p.returncode != 0:
        die(f"git {' '.join(args)} 失败: {p.stderr.strip()[:400]}")
    return p.stdout


def api(method: str, path: str, token: str, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(
        API + path,
        data=data,
        method=method,
        headers={
            "Authorization": f"Bearer {token}",
            "User-Agent": UA,
            "Accept": "application/vnd.github+json",
            **({"Content-Type": "application/json"} if data else {}),
        },
    )
    if DRY and method != "GET":
        info(f"DRY-RUN 会请求 {method} {path}")
        return 0, None
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, None


def err(d) -> str:
    return "" if not d else f"{d.get('message','')} {d.get('errors','')}"


def local_tree(repo: pathlib.Path) -> dict[str, tuple[str, str]]:
    """返回 {path: (mode, blob_sha)}，mode 保留可执行位/符号链接。"""
    raw = subprocess.run(
        ["git", "-C", str(repo), "ls-tree", "-r", "-z", "HEAD"],
        capture_output=True, text=True,
    ).stdout
    out: dict[str, tuple[str, str]] = {}
    for rec in raw.split("\0"):
        if not rec:
            continue
        meta, _, path = rec.partition("\t")
        mode, _type, sha = meta.split()
        out[path] = (mode, sha)
    return out


def main() -> int:
    global DRY
    ap = argparse.ArgumentParser(description="用 Git Data API 推送本地 HEAD 到远端分支")
    ap.add_argument("--repo", required=True, help="owner/name")
    ap.add_argument("--dir", default=".", help="本地 git 仓库目录")
    ap.add_argument("--branch", default="main")
    ap.add_argument("--token-file", default="build/.github-token")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    DRY = args.dry_run

    repo = pathlib.Path(args.dir).resolve()
    owner, name = args.repo.split("/", 1)

    token = ""
    for var in ("GITHUB_TOKEN", "GH_TOKEN"):
        v = os.environ.get(var, "").strip()
        if v:
            token = v
            break
    if not token:
        tf = pathlib.Path(args.token_file)
        if tf.is_file():
            token = tf.read_text(encoding="utf-8").strip()
    if not token:
        die("找不到令牌，请设置 GITHUB_TOKEN 或写入 --token-file")

    local_sha = git(repo, "rev-parse", "HEAD").strip()
    info(f"本地 HEAD = {local_sha[:12]}")

    # 提交元数据：作者/提交者/时间戳都照搬本地提交
    meta = git(repo, "log", "-1",
               "--format=%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%B").split("\0")
    a_name, a_email, a_date, c_name, c_email, c_date, message = (
        meta[0], meta[1], meta[2], meta[3], meta[4], meta[5], meta[6].rstrip("\n")
    )

    st, ref = api("GET", f"/repos/{owner}/{name}/git/ref/heads/{args.branch}", token)
    if st != 200:
        die(f"取远端分支失败 HTTP {st}: {err(ref)}")
    remote_sha = ref["object"]["sha"]
    info(f"远端 {args.branch} = {remote_sha[:12]}")

    if remote_sha == local_sha:
        info("两边已经一致，无需推送")
        return 0

    st, base = api("GET", f"/repos/{owner}/{name}/git/commits/{remote_sha}", token)
    if st != 200:
        die(f"取远端提交失败 HTTP {st}: {err(base)}")
    base_tree = base["tree"]["sha"]

    # 远端是否已经是本地的祖先——不是的话推进分支会丢提交，必须停下
    st, cmp = api("GET", f"/repos/{owner}/{name}/compare/{remote_sha}...{local_sha}", token)
    if st == 200 and cmp.get("status") not in ("ahead", "identical"):
        die(f"远端与本地已分叉（status={cmp.get('status')}），"
            "API 推送只做 fast-forward，请先用 git 处理")

    st, rtree = api("GET", f"/repos/{owner}/{name}/git/trees/{base_tree}?recursive=1", token)
    if st != 200:
        die(f"取远端树失败 HTTP {st}: {err(rtree)}")
    if rtree.get("truncated"):
        die("远端树被截断，这个工具不适合超大仓库")
    remote_tree = {e["path"]: (e["mode"], e["sha"])
                   for e in rtree["tree"] if e["type"] == "blob"}

    ltree = local_tree(repo)
    info(f"本地 {len(ltree)} 个文件，远端 {len(remote_tree)} 个文件")

    entries = []
    for path, (mode, sha) in sorted(ltree.items()):
        if remote_tree.get(path) == (mode, sha):
            continue
        if DRY:
            info(f"DRY-RUN 会上传 {path} ({mode})")
            entries.append({"path": path, "mode": mode, "type": "blob", "sha": "dry-run"})
            continue
        content = subprocess.run(
            ["git", "-C", str(repo), "cat-file", "blob", sha],
            capture_output=True,
        ).stdout
        st, blob = api("POST", f"/repos/{owner}/{name}/git/blobs", token,
                       {"content": base64.b64encode(content).decode(), "encoding": "base64"})
        if st not in (200, 201):
            die(f"上传 blob {path} 失败 HTTP {st}: {err(blob)}")
        info(f"  新增/更新 {path}  ({len(content):,} B)")
        entries.append({"path": path, "mode": mode, "type": "blob", "sha": blob["sha"]})

    for path in sorted(set(remote_tree) - set(ltree)):
        info(f"  删除 {path}")
        entries.append({"path": path, "mode": remote_tree[path][0],
                        "type": "blob", "sha": None})

    if not entries:
        info("没有差异，无需推送")
        return 0

    st, tree = api("POST", f"/repos/{owner}/{name}/git/trees", token,
                   {"base_tree": base_tree, "tree": entries})
    if st not in (200, 201):
        die(f"建 tree 失败 HTTP {st}: {err(tree)}")

    st, commit = api("POST", f"/repos/{owner}/{name}/git/commits", token, {
        "message": message,
        "tree": tree["sha"],
        "parents": [remote_sha],
        "author": {"name": a_name, "email": a_email, "date": a_date},
        "committer": {"name": c_name, "email": c_email, "date": c_date},
    })
    if st not in (200, 201):
        die(f"建 commit 失败 HTTP {st}: {err(commit)}")
    info(f"远端新提交 = {commit['sha'][:12]}")

    st, upd = api("PATCH", f"/repos/{owner}/{name}/git/refs/heads/{args.branch}", token,
                  {"sha": commit["sha"], "force": False})
    if st != 200:
        die(f"推进分支失败 HTTP {st}: {err(upd)}")
    info(f"已推进 {args.branch} -> {commit['sha'][:12]}")
    info("完成。注意：本地提交与远端新提交内容相同但 SHA 不同，"
         "网络恢复后跑一次 git fetch && git reset --hard origin/"
         f"{args.branch} 即可对齐。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
