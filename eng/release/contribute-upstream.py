#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 AudioCpp.NET 登记到上游 ``0xShug0/audio.cpp`` 的 README ``## Projects`` 列表。

上游 README 在该章节写着"Have a project using audio.cpp? Submit a PR"，
本项目就走这条路：fork 上游 → 只改 README 一行 → 开 PR。

为什么不 clone 上游
------------------
上游仓库很大，本地 clone 再 push 毫无必要。本脚本全程走 GitHub REST API
的 Git Data API（blobs → trees → commits → refs），只上传 1 个 blob，
不落地任何工作树。只依赖 Python 标准库，Bash / PowerShell 两个通道都能跑。

为什么不用 gh CLI
-----------------
本机没有安装 gh（与 ``publish-github.py`` 保持一致的做法）。

令牌读取顺序
------------
刻意**不支持**命令行传参，避免令牌泄漏到进程列表和 shell 历史：

  1. 环境变量 ``GITHUB_TOKEN`` 或 ``GH_TOKEN``
  2. 文件 ``--token-file``（默认 ``build/.github-token``，build/ 已在 .gitignore 内）

需要的令牌权限
--------------
fork 上游 + 在 fork 里建分支 + 开 PR，classic PAT 的 ``public_repo``（或 ``repo``）即可。
仓库自带的 OAuth 令牌（scope 含 ``repo``）已验证可用。

用法
----
::

    # 1. 先干跑：只打印将要提交的那一行 diff，不动远程
    python eng/release/contribute-upstream.py --dry-run

    # 2. 确认无误后真正提交（fork + 建分支 + 开 PR）
    GITHUB_TOKEN=xxx python eng/release/contribute-upstream.py --submit

安全默认
--------
**不加 ``--submit`` 就绝不写远程**，不加 ``--dry-run`` 也一样，
远程写入必须显式声明。这与 ``publish-github.py`` 的默认值不同是有意为之：
这里是往别人的仓库提交内容，默认应当是只读的。
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import pathlib
import sys
import time
import urllib.error
import urllib.request

API = "https://api.github.com"
UA = "audiocpp-dotnet-contribute"

# 上游仓库
UPSTREAM = "0xShug0/audio.cpp"

# 我们自己的仓库（PR 里指向的地址）
OUR_REPO = "dongfangzhizhu/AudioCpp.NET"

# 默认分支名：README 里那一行的写法，保持动词风格与相邻条目一致
BRANCH = "docs/list-audiocpp-dotnet-project"

# 目标章节
SECTION = "## Projects"

# 要插入的条目（单行，与相邻条目同为"一句话说明"的写法）
ENTRY = (
    f"- [AudioCpp.NET](https://github.com/{OUR_REPO}) provides .NET 10 bindings for "
    "audio.cpp behind a small versioned C ABI shim, shipped as managed, CPU runtime, "
    "and CUDA runtime NuGet packages for Windows and Linux."
)

PR_TITLE = "docs: add AudioCpp.NET to the Projects list"

PR_BODY = f"""### What this PR does

Adds one entry to the `## Projects` list in `README.md`:

```markdown
{ENTRY}
```

Nothing else is touched — no code, no docs, no CI.

### What the project is

[AudioCpp.NET](https://github.com/{OUR_REPO}) is a .NET 10 wrapper around the audio.cpp
engine. It talks to the native side through a small, versioned C ABI shim over
`engine_runtime` rather than binding C++ symbols directly, so the surface stays stable
across upstream releases.

- **Packages**: `AudioCpp.NET` (managed API), `AudioCpp.NET.Runtime` (CPU native shim),
  `AudioCpp.NET.Runtime.Cuda` (CUDA native shim) — on nuget.org.
- **RIDs**: `win-x64` and `linux-x64`.
- **Coverage**: wraps every model family the pinned engine defines — 74 `model_specs`
  families produce 74 linked loaders and a 76-entry catalog (74 + 2 built-in VADs),
  with 217 downloadable packages. Verified on four platform cells:
  Windows/Linux × CPU/CUDA.
- **License**: Apache-2.0, inherited from upstream (see `NOTICE`).

### Upstream pinning

The wrapper targets a pinned upstream commit
(`78d47706c30ef215ba9ad3559baff309efeb5260`, recorded in `eng/upstream.lock.json`) so it
never builds a moving `main`. It does not redistribute model weights; the companion CLI
downloads them separately.

### Why it fits this list

It is an independent, maintained binding that makes audio.cpp consumable from the .NET
ecosystem, in the same spirit as the existing Python wrapper entry.
"""


# --------------------------------------------------------------------------- #
# 小工具
# --------------------------------------------------------------------------- #
def info(msg: str) -> None:
    print(f":: {msg}", flush=True)


def warn(msg: str) -> None:
    print(f"!! {msg}", flush=True)


def die(msg: str, code: int = 1) -> None:
    print(f"!!! {msg}", file=sys.stderr, flush=True)
    sys.exit(code)


def api(method: str, url: str, token: str, body=None, accept="application/vnd.github+json"):
    """返回 (status, headers, parsed_json_or_None)。4xx/5xx 不抛异常，交给调用方判断。"""
    data = None
    headers = {
        "Accept": accept,
        "User-Agent": UA,
        "Authorization": f"Bearer {token}",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    elif method in ("POST", "PUT", "PATCH"):
        data = b""

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
            parsed = None
            if raw and accept != "application/vnd.github.raw":
                try:
                    parsed = json.loads(raw.decode("utf-8"))
                except ValueError:
                    parsed = None
            return resp.status, dict(resp.headers), parsed
    except urllib.error.HTTPError as e:
        raw = e.read()
        parsed = None
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except Exception:
            parsed = raw.decode("utf-8", "replace")[:2000]
        return e.code, dict(e.headers), parsed
    except urllib.error.URLError as e:
        die(f"网络请求失败: {method} {url}\n    {e.reason}")


def err_text(d) -> str:
    if isinstance(d, dict):
        return f"{d.get('message', '')} {d.get('errors', '')}".strip()
    return str(d)[:500]


def read_token(args) -> str:
    for var in ("GITHUB_TOKEN", "GH_TOKEN"):
        v = os.environ.get(var)
        if v and v.strip():
            info(f"从环境变量 {var} 读取令牌")
            return v.strip()
    tf = pathlib.Path(args.token_file)
    if tf.is_file():
        v = tf.read_text(encoding="utf-8", errors="replace").strip()
        if v:
            info(f"从文件读取令牌: {tf}")
            return v
    die(
        "找不到 GitHub 令牌。请设置环境变量 GITHUB_TOKEN，\n"
        f"    或把令牌写入 {tf}（该路径已在 .gitignore 内）。"
    )
    raise SystemExit(1)


def check_token(token: str) -> str:
    st, hdrs, d = api("GET", f"{API}/user", token)
    if st != 200:
        die(f"令牌无效或权限不足（HTTP {st}）：{err_text(d)}")
    info(f"已认证为 {d['login']}")
    scopes = hdrs.get("X-OAuth-Scopes", "")
    if scopes:
        info(f"令牌 scopes: {scopes}")
        if "public_repo" not in scopes and "repo" not in scopes:
            die("令牌既没有 repo 也没有 public_repo scope，无法 fork / 开 PR")
    else:
        warn("响应头没有 X-OAuth-Scopes（细粒度令牌正常），继续。")
    return d["login"]


# --------------------------------------------------------------------------- #
# README 处理
# --------------------------------------------------------------------------- #
def fetch_upstream_readme(token: str) -> tuple[str, str]:
    """取上游默认分支的 README 原文（原始字节 → UTF-8 文本）。返回 (文本, 默认分支)。"""
    st, _, d = api("GET", f"{API}/repos/{UPSTREAM}", token)
    if st != 200:
        die(f"读不到上游仓库 {UPSTREAM}（HTTP {st}）")
    default_branch = d["default_branch"]

    url = f"{API}/repos/{UPSTREAM}/readme"
    req = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github.raw",
            "User-Agent": UA,
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        text = resp.read().decode("utf-8")
    info(f"已取上游 README（{len(text)} 字符，默认分支 {default_branch}）")
    return text, default_branch


def insert_entry(text: str) -> tuple[str, list[str], int]:
    """在 ``## Projects`` 章节的最后一个 ``- `` 条目之后插入 ENTRY。

    返回 (新文本, 用于展示的上下文行, 新条目在 1 基行号)。
    """
    if OUR_REPO.lower() in text.lower():
        die(f"README 里已经出现 {OUR_REPO}，无需重复提交。")

    lines = text.split("\n")
    try:
        hi = next(i for i, l in enumerate(lines) if l.strip() == SECTION)
    except StopIteration:
        die(f"在上游 README 里找不到章节 {SECTION!r} —— 上游可能改过结构，请人工确认。")

    ei = next((i for i in range(hi + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    bullets = [i for i in range(hi, ei) if lines[i].startswith("- ")]
    if not bullets:
        die(f"{SECTION} 章节里没有任何 `- ` 条目，结构可能已变，请人工确认。")

    li = bullets[-1]
    new_lines = lines[: li + 1] + [ENTRY] + lines[li + 1 :]
    entry_line_no = li + 2  # 1 基，且已算上插入的自身
    ctx = new_lines[li - 1 : li + 3]
    return "\n".join(new_lines), ctx, entry_line_no


# --------------------------------------------------------------------------- #
# GitHub 写操作
# --------------------------------------------------------------------------- #
def ensure_fork(login: str, token: str) -> str:
    """确保 ``login/audio.cpp`` 这个 fork 存在，返回仓库全名。"""
    repo = f"{login}/audio.cpp"
    st, _, d = api("GET", f"{API}/repos/{repo}", token)
    if st == 200:
        parent = (d.get("parent") or {}).get("full_name")
        if parent and parent.lower() != UPSTREAM.lower():
            die(f"{repo} 已存在，但它的上游是 {parent} 而不是 {UPSTREAM}，不敢复用。")
        info(f"复用已有 fork: {repo}")
        return repo
    if st != 404:
        die(f"查询 {repo} 失败（HTTP {st}）：{err_text(d)}")

    info(f"创建 fork: {UPSTREAM} → {repo}")
    st, _, d = api("POST", f"{API}/repos/{UPSTREAM}/forks", token, body={"default_branch_only": True})
    if st not in (200, 201, 202):
        die(f"创建 fork 失败（HTTP {st}）：{err_text(d)}")

    info("等待 fork 就绪（大仓库可能要一两分钟）…")
    for _ in range(40):
        st, _, d = api("GET", f"{API}/repos/{repo}", token)
        if st == 200:
            info(f"fork 就绪: {repo}")
            return repo
        time.sleep(5)
    die(f"fork {repo} 在 200 秒内没有就绪，请稍后重试（脚本会复用已建好的 fork）。")
    raise SystemExit(1)


def create_branch_with_readme(fork: str, base_branch: str, new_readme: str,
                              commit_msg: str, token: str) -> str:
    """用 Git Data API 在 fork 上建一个只改 README 的提交分支，返回新提交 SHA。"""
    st, _, d = api("GET", f"{API}/repos/{UPSTREAM}/git/ref/heads/{base_branch}", token)
    if st != 200:
        die(f"取上游 {base_branch} 失败（HTTP {st}）：{err_text(d)}")
    base_sha = d["object"]["sha"]

    st, _, d = api("GET", f"{API}/repos/{UPSTREAM}/git/commits/{base_sha}", token)
    if st != 200:
        die(f"取上游基线提交失败（HTTP {st}）")
    base_tree = d["tree"]["sha"]
    info(f"上游基线: {base_sha[:12]}  (tree {base_tree[:12]})")

    raw = new_readme.encode("utf-8")
    st, _, d = api("POST", f"{API}/repos/{fork}/git/blobs", token,
                   body={"content": base64.b64encode(raw).decode(), "encoding": "base64"})
    if st != 201:
        die(f"建 blob 失败（HTTP {st}）：{err_text(d)}")
    blob = d["sha"]
    info(f"建 blob: {blob[:12]}  ({len(raw)} 字节)")

    st, _, d = api("POST", f"{API}/repos/{fork}/git/trees", token, body={
        "base_tree": base_tree,
        "tree": [{"path": "README.md", "mode": "100644", "type": "blob", "sha": blob}],
    })
    if st != 201:
        die(f"建 tree 失败（HTTP {st}）：{err_text(d)}")
    tree = d["sha"]

    st, _, d = api("POST", f"{API}/repos/{fork}/git/commits", token, body={
        "message": commit_msg, "tree": tree, "parents": [base_sha],
    })
    if st != 201:
        die(f"建 commit 失败（HTTP {st}）：{err_text(d)}")
    commit = d["sha"]
    info(f"建 commit: {commit[:12]}")

    ref = f"refs/heads/{BRANCH}"
    st, _, d = api("POST", f"{API}/repos/{fork}/git/refs", token,
                   body={"ref": ref, "sha": commit})
    if st == 201:
        info(f"建分支: {BRANCH}")
    elif st == 422:
        # 分支已存在（重复运行）：快进到新提交
        st2, _, d2 = api("PATCH", f"{API}/repos/{fork}/git/refs/heads/{BRANCH}", token,
                         body={"sha": commit, "force": True})
        if st2 != 200:
            die(f"分支 {BRANCH} 已存在且更新失败（HTTP {st2}）：{err_text(d2)}")
        info(f"分支 {BRANCH} 已存在，已更新到新提交")
    else:
        die(f"建分支失败（HTTP {st}）：{err_text(d)}")
    return commit


def open_pr(login: str, title: str, body: str, token: str) -> str:
    # 已存在同源分支的 PR 就复用，避免重复运行报错
    st, _, d = api("GET", f"{API}/repos/{UPSTREAM}/pulls?head={login}:{BRANCH}&state=all", token)
    if st == 200 and isinstance(d, list) and d:
        url = d[0]["html_url"]
        warn(f"该分支已有 PR，直接复用: {url}")
        return url

    st, _, d = api("POST", f"{API}/repos/{UPSTREAM}/pulls", token, body={
        "title": title, "head": f"{login}:{BRANCH}", "base": "main",
        "body": body, "maintainer_can_modify": True,
    })
    if st != 201:
        die(f"开 PR 失败（HTTP {st}）：{err_text(d)}\n    分支已推送，可稍后手动开 PR。")
    info(f"PR 已创建: {d['html_url']}")
    return d["html_url"]


# --------------------------------------------------------------------------- #
# 主流程
# --------------------------------------------------------------------------- #
def main() -> int:
    ap = argparse.ArgumentParser(
        description="向上游 0xShug0/audio.cpp 提交 AudioCpp.NET 的 Projects 条目 PR",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--submit", action="store_true",
                    help="真正写远程（fork / 建分支 / 开 PR）。不加则只做干跑。")
    ap.add_argument("--dry-run", action="store_true",
                    help="显式干跑（默认行为，保留此开关是为了和其他脚本用法一致）。")
    ap.add_argument("--token-file", default="build/.github-token", help="令牌文件路径")
    ap.add_argument("--base", default=None, help="上游基线分支（默认取默认分支）")
    ap.add_argument("--commit-message", default=None, help="提交信息")
    ap.add_argument("--title", default=PR_TITLE, help="PR 标题")
    ap.add_argument("--body-file", default=None, help="PR 正文文件（默认用脚本内置正文）")
    args = ap.parse_args()

    write = args.submit and not args.dry_run

    token = read_token(args)
    login = check_token(token)

    readme, default_branch = fetch_upstream_readme(token)
    base_branch = args.base or default_branch
    new_readme, ctx, entry_line_no = insert_entry(readme)

    print()
    info(f"在第 {entry_line_no} 行插入 1 行（仅新增，不修改任何现有行）:")
    for line in ctx:
        mark = ">>>" if line == ENTRY else "   "
        print(f"  {mark} {line[:160]}")
    print()

    if not write:
        info("干跑模式：不会写远程。确认无误后加 --submit 重跑。")
        return 0

    commit_msg = args.commit_message or (
        "docs: add AudioCpp.NET to the Projects list\n\n"
        f"Adds {OUR_REPO} to the \"Projects\" section, which invites PRs for\n"
        "projects built on audio.cpp."
    )
    if args.body_file:
        body = pathlib.Path(args.body_file).read_text(encoding="utf-8")
    else:
        body = PR_BODY

    fork = ensure_fork(login, token)
    create_branch_with_readme(fork, base_branch, new_readme, commit_msg, token)
    url = open_pr(login, args.title, body, token)

    print()
    info(f"完成。PR: {url}")
    info(f"分支: https://github.com/{fork}/tree/{BRANCH}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print()
        die("被中断。")
