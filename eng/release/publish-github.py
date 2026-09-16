#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GitHub 发布助手：建仓库 / 推代码 / 打标签 / 建 Release / 传原生归档。

为什么不用 gh CLI
-----------------
本机没有安装 gh。这里直接用 GitHub REST API，只依赖 Python 标准库，
Bash 和 PowerShell 两个通道都能跑（避免再踩"某个通道不认某个工具"的坑）。

令牌读取顺序
------------
刻意**不支持**命令行传参，避免令牌泄漏到进程列表和 shell 历史：

  1. 环境变量 ``GITHUB_TOKEN`` 或 ``GH_TOKEN``
  2. 文件 ``--token-file``（默认 ``build/.github-token``，build/ 已在 .gitignore 内）

需要的 PAT 权限
---------------
本仓库含 ``.github/workflows/*.yml``，**推送 workflow 文件需要额外权限**，
这是最容易踩的坑：

  * classic PAT   ：勾 ``repo`` + ``workflow``
  * fine-grained  ：Contents=Read/Write、Administration=Read/Write（建仓库）、
                    Workflows=Read/Write（推 workflow 文件）

用法示例
--------
::

    # 只建仓库并推 main（最常用）
    GITHUB_TOKEN=xxx python eng/release/publish-github.py \\
        --repo dongfangzhizhu/AudioCpp.NET --create-repo --push

    # 另外打标签并建 Release、挂上原生归档
    GITHUB_TOKEN=xxx python eng/release/publish-github.py \\
        --repo dongfangzhizhu/AudioCpp.NET --push \\
        --tag v0.1.0 --release --assets build/native-archives

    # 先看会做什么，不动远程
    python eng/release/publish-github.py --repo o/n --create-repo --push --dry-run
"""

from __future__ import annotations

import argparse
import json
import mimetypes
import os
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"
UA = "audiocpp-dotnet-release"

DRY = False


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


def run(cmd: list[str], cwd: pathlib.Path, check: bool = True, quiet: bool = False):
    """跑一个外部命令。凭据只通过环境/临时配置传递，不写进命令行。"""
    if quiet:
        shown = cmd
    else:
        shown = cmd
    if DRY:
        info("DRY-RUN 会执行: " + " ".join(shown))
        return subprocess.CompletedProcess(cmd, 0, "", "")
    proc = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True)
    if proc.returncode != 0 and check:
        warn(f"命令失败({proc.returncode}): {' '.join(shown)}")
        if proc.stdout.strip():
            print(proc.stdout.strip()[:4000])
        if proc.stderr.strip():
            print(proc.stderr.strip()[:4000], file=sys.stderr)
        sys.exit(proc.returncode)
    return proc


# --------------------------------------------------------------------------- #
# REST API
# --------------------------------------------------------------------------- #
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

    if DRY and method != "GET":
        info(f"DRY-RUN 会请求: {method} {url}")
        return 0, {}, None

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            raw = r.read()
            parsed = None
            if raw:
                try:
                    parsed = json.loads(raw.decode("utf-8"))
                except Exception:
                    parsed = None
            return r.status, dict(r.headers), parsed
    except urllib.error.HTTPError as e:
        raw = e.read()
        parsed = None
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except Exception:
            pass
        return e.code, dict(e.headers or {}), parsed
    except Exception as e:  # 网络层
        return 0, {}, {"exception": f"{type(e).__name__}: {e}"}


def err_text(parsed) -> str:
    if not parsed:
        return ""
    if "exception" in parsed:
        return parsed["exception"]
    return parsed.get("message", "") + " " + str(parsed.get("errors", ""))


# --------------------------------------------------------------------------- #
# 令牌
# --------------------------------------------------------------------------- #
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
        f"    或把令牌写入 {tf}（该路径已在 .gitignore 内）。\n"
        "    需要 classic PAT 的 repo+workflow 权限。"
    )
    raise SystemExit(1)


# --------------------------------------------------------------------------- #
# 各步骤
# --------------------------------------------------------------------------- #
def check_token(token: str):
    st, _, d = api("GET", f"{API}/user", token)
    if st != 200:
        die(f"令牌无效或权限不足（HTTP {st}）：{err_text(d)}")
    info(f"已认证为 {d['login']}  (scopes 见响应头)")
    st2, hdrs, _ = api("GET", f"{API}/user", token)
    scopes = hdrs.get("X-OAuth-Scopes", "")
    if scopes:
        info(f"令牌 scopes: {scopes}")
        if "workflow" not in scopes and "repo" not in scopes:
            warn("令牌既没有 repo 也没有 workflow scope，推送 workflow 文件会被拒")
        elif "workflow" not in scopes:
            warn("令牌没有 workflow scope —— 仓库含 .github/workflows/*.yml，推送可能被拒")
    return d["login"]


def ensure_repo(owner: str, name: str, token: str, private: bool, description: str) -> str:
    """仓库不存在就建。返回它的 html_url。"""
    st, _, d = api("GET", f"{API}/repos/{owner}/{name}", token)
    if st == 200:
        info(f"仓库已存在: {d['html_url']}  (private={d['private']})")
        return d["html_url"]
    if st != 404:
        die(f"查询仓库失败 HTTP {st}: {err_text(d)}")

    info(f"仓库 {owner}/{name} 不存在，准备创建")
    body = {
        "name": name,
        "description": description,
        "private": private,
        "has_issues": True,
        "has_wiki": False,
        "has_projects": False,
        "auto_init": False,  # 不加 README，否则本地已提交的历史会冲突
    }
    st, _, d = api("POST", f"{API}/user/repos", token, body=body)
    if st not in (200, 201):
        die(f"创建仓库失败 HTTP {st}: {err_text(d)}")
    info(f"仓库已创建: {d['html_url']}")
    return d["html_url"]


def git_setup_remote(repo_dir: pathlib.Path, slug: str, token: str, dry: bool) -> str:
    """配置 origin。

    关键点：**绝不把令牌写进 .git/config**。这里用一个只在本条命令内生效的
    配置注入（git -c http.<url>.extraheader），推送完成后 .git/config 里
    只留下干净的 https://github.com/... 地址。
    """
    clean_url = f"https://github.com/{slug}.git"
    exist = run(["git", "remote"], cwd=repo_dir, check=False, quiet=True)
    remotes = (exist.stdout or "").split()
    if "origin" in remotes:
        cur = run(["git", "remote", "get-url", "origin"], cwd=repo_dir, check=False).stdout.strip()
        info(f"origin 已存在: {cur}")
        if cur != clean_url:
            info(f"更新 origin -> {clean_url}")
            run(["git", "remote", "set-url", "origin", clean_url], cwd=repo_dir)
    else:
        info(f"添加 origin -> {clean_url}")
        run(["git", "remote", "add", "origin", clean_url], cwd=repo_dir)
    return clean_url


def git_push(repo_dir: pathlib.Path, refspec: str, token: str, slug: str) -> None:
    """带令牌推送，令牌只存在于本次进程的参数里，不落盘。

    强制打开 TLS 校验：本机全局配置里 http.sslverify=false（多半是为了某个
    内网镜像），但带着令牌推送时不该关掉证书校验。只有当代理确实做了 TLS
    拦截时才用 AUDIOCPP_GIT_INSECURE=1 放行。
    """
    clean_url = f"https://github.com/{slug}.git"
    run(["git", "remote", "set-url", "origin", clean_url], cwd=repo_dir, check=False)
    if DRY:
        info(f"DRY-RUN 会推送 {refspec} 到 {clean_url}")
        return
    insecure = os.environ.get("AUDIOCPP_GIT_INSECURE", "") not in ("", "0")
    if insecure:
        warn("AUDIOCPP_GIT_INSECURE 已设置：本次推送跳过 TLS 证书校验")
    b64 = __import__("base64").b64encode(f"x-access-token:{token}".encode()).decode()
    proc = subprocess.run(
        [
            "git",
            "-c",
            f"http.sslverify={'false' if insecure else 'true'}",
            "-c",
            f"http.https://github.com/.extraheader=Authorization: Basic {b64}",
            "push",
            clean_url,
            refspec,
        ],
        cwd=str(repo_dir),
        capture_output=True,
        text=True,
    )
    # 回显时把任何可能的令牌擦掉
    out = (proc.stdout or "") + (proc.stderr or "")
    out = out.replace(token, "***")
    if proc.returncode != 0:
        warn(f"推送 {refspec} 失败：")
        print(out.strip()[:6000])
        sys.exit(proc.returncode)
    tail = [ln for ln in out.strip().splitlines() if ln.strip()][-6:]
    for ln in tail:
        info(ln)
    info(f"推送成功: {refspec}")


def find_release(owner: str, name: str, tag: str, token: str):
    st, _, d = api("GET", f"{API}/repos/{owner}/{name}/releases/tags/{tag}", token)
    return d if st == 200 else None


def create_release(owner: str, name: str, tag: str, token: str, notes: str,
                   prerelease: bool, draft: bool = False) -> dict:
    st, _, d = api("GET", f"{API}/repos/{owner}/{name}/releases/tags/{tag}", token)
    if st == 200:
        info(f"Release {tag} 已存在: {d['html_url']}{' (draft)' if d.get('draft') else ''}")
        return d
    info(f"创建{'草稿 ' if draft else ''}Release {tag}")
    body = {
        "tag_name": tag,
        "name": tag,
        "body": notes,
        "draft": draft,
        "prerelease": prerelease,
    }
    st, _, d = api("POST", f"{API}/repos/{owner}/{name}/releases", token, body=body)
    if st not in (200, 201):
        die(f"创建 Release 失败 HTTP {st}: {err_text(d)}")
    info(f"Release 已创建: {d['html_url']}")
    return d


def publish_release(owner: str, name: str, release_id: int, tag: str, token: str) -> None:
    """把草稿 Release 转为已发布。这一步才会创建标签、触发工作流。"""
    info(f"发布 Release {tag}（草稿 -> 正式，此时才创建标签并触发工作流）")
    st, _, d = api("PATCH", f"{API}/repos/{owner}/{name}/releases/{release_id}",
                   token, body={"draft": False})
    if st != 200:
        die(f"发布 Release 失败 HTTP {st}: {err_text(d)}")
    info(f"Release 已发布: {d['html_url']}")


def upload_assets(owner: str, name: str, release: dict, assets_dir: pathlib.Path, token: str) -> int:
    files = sorted(p for p in assets_dir.glob("*.zip") if p.is_file())
    if not files:
        warn(f"{assets_dir} 下没有 .zip，跳过附件上传")
        return 0
    existing = {a["name"] for a in release.get("assets", [])}
    ok = 0
    for f in files:
        if f.name in existing:
            info(f"附件已存在，跳过: {f.name}")
            ok += 1
            continue
        url = f"{UPLOADS}/repos/{owner}/{name}/releases/{release['id']}/assets?name={f.name}"
        if DRY:
            info(f"DRY-RUN 会上传 {f.name} ({f.stat().st_size:,} B)")
            ok += 1
            continue
        ctype = mimetypes.guess_type(f.name)[0] or "application/zip"
        data = f.read_bytes()
        t0 = time.time()
        # api() 只支持 JSON body；附件必须传原始二进制，所以这里手写请求。
        req = urllib.request.Request(
            url,
            data=data,
            method="POST",
            headers={
                "Authorization": f"Bearer {token}",
                "User-Agent": UA,
                "Accept": "application/vnd.github+json",
                "Content-Type": ctype,
                "Content-Length": str(len(data)),
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=1800) as r:
                st = r.status
        except urllib.error.HTTPError as e:
            st = e.code
            warn(f"上传 {f.name} 失败 HTTP {st}: {e.read()[:300]}")
            continue
        dt = time.time() - t0
        info(f"已上传 {f.name} ({len(data):,} B, {dt:.1f}s)")
        ok += 1
    return ok


def set_secret(owner: str, name: str, token: str, key: str, value: str) -> bool:
    """通过 API 写仓库 Secret（Actions secret）。需要 PyNaCl 做 sealed box 加密。"""
    try:
        from nacl import encoding, public  # type: ignore
    except Exception:
        return False
    st, _, d = api("GET", f"{API}/repos/{owner}/{name}/actions/secrets/public-key", token)
    if st != 200:
        warn(f"取公钥失败 HTTP {st}: {err_text(d)}")
        return False
    pk = public.PublicKey(d["key"].encode("utf-8"), encoding.Base64Encoder())
    sealed = public.SealedBox(pk).encrypt(value.encode("utf-8"))
    import base64 as _b64

    body = {"encrypted_value": _b64.b64encode(sealed).decode(), "key_id": d["key_id"]}
    st, _, r = api("PUT", f"{API}/repos/{owner}/{name}/actions/secrets/{key}", token, body=body)
    if st in (201, 204):
        info(f"已写入仓库 Secret: {key}")
        return True
    warn(f"写 Secret {key} 失败 HTTP {st}: {err_text(r)}")
    return False


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def main() -> int:
    global DRY
    ap = argparse.ArgumentParser(description="GitHub 发布助手（建仓库 / 推送 / 建 Release / 传附件）")
    ap.add_argument("--repo", required=True, help="owner/name，例如 dongfangzhizhu/AudioCpp.NET")
    ap.add_argument("--dir", default=".", help="本地 git 仓库目录（默认当前目录）")
    ap.add_argument("--branch", default="main", help="主分支名（默认 main）")
    ap.add_argument("--token-file", default="build/.github-token", help="令牌文件路径")
    ap.add_argument("--create-repo", action="store_true", help="仓库不存在时创建")
    ap.add_argument("--private", action="store_true", help="创建为私有仓库")
    ap.add_argument("--description", default=".NET 10 bindings for audio.cpp (ASR / VAD / TTS / audio tagging)",
                    help="仓库描述")
    ap.add_argument("--push", action="store_true", help="推送 --branch 到 origin")
    ap.add_argument("--tag", default="", help="要创建并推送的标签，例如 v0.1.0")
    ap.add_argument("--release", action="store_true", help="创建 GitHub Release")
    ap.add_argument("--draft-first", action="store_true",
                    help="先建草稿 Release、传完附件再转正式。草稿不会创建标签，"
                         "所以不会在附件就位前触发依赖附件的工作流（release.yml）。")
    ap.add_argument("--assets", default="", help="Release 附件目录（上传其中的 *.zip）")
    ap.add_argument("--notes-file", default="", help="Release 说明文件")
    ap.add_argument("--prerelease", action="store_true", help="标记为预发布")
    ap.add_argument("--set-secret", action="append", default=[],
                    help="写仓库 Secret，格式 NAME=value（可重复；需要 PyNaCl）")
    ap.add_argument("--verify", action="store_true", help="只做只读体检（认证 + 仓库状态）")
    ap.add_argument("--dry-run", action="store_true", help="只打印会做什么，不改远程")
    args = ap.parse_args()

    DRY = args.dry_run
    repo_dir = pathlib.Path(args.dir).resolve()
    if not (repo_dir / ".git").exists():
        die(f"{repo_dir} 不是一个 git 仓库")
    if "/" not in args.repo:
        die("--repo 必须是 owner/name 形式")
    owner, name = args.repo.split("/", 1)

    token = read_token(args)
    login = check_token(token)

    if args.verify:
        return 0

    if owner.lower() != login.lower():
        warn(f"令牌属于 {login}，但目标是 {owner}/{name} —— 只有组织管理员或本人才能建仓")

    if args.create_repo:
        ensure_repo(owner, name, token, args.private, args.description)
        time.sleep(1)  # 让新仓库在 API 侧可见，避免紧接的 push 撞 404
    else:
        st, _, d = api("GET", f"{API}/repos/{owner}/{name}", token)
        if st != 200:
            die(f"仓库 {args.repo} 不可访问（HTTP {st}）：{err_text(d)}。加 --create-repo 可自动创建。")

    slug = f"{owner}/{name}"

    if args.push or args.tag:
        git_setup_remote(repo_dir, slug, token, DRY)

    if args.push:
        git_push(repo_dir, f"{args.branch}:{args.branch}", token, slug)

    # Secret 必须最先写：release.yml 的 publish 任务在缺 NUGET_API_KEY 时是硬失败
    # （直接 exit 1），晚于标签触发就白跑一次。
    for spec in args.set_secret:
        if "=" in spec:
            k, v = spec.split("=", 1)
        else:
            # 只给名字时从环境变量读，避免密钥出现在命令行参数（进程列表可见）
            k, v = spec, os.environ.get(spec, "")
        if not k or not v:
            warn(f"--set-secret {spec}: 既没给 value，环境变量里也没有 {spec}，跳过")
            continue
        if not set_secret(owner, name, token, k, v):
            warn(f"未能写入 {k}（多半是缺 PyNaCl）。可改用网页："
                 f"https://github.com/{slug}/settings/secrets/actions")

    # 顺序很重要：Release 和附件必须先就位，标签最后推。
    # release.yml 由标签触发，它要从 Release 拉 audiocpp-native-*.zip；先推标签
    # 会让工作流跑到拉取步骤时附件还不存在，那次运行就只能发托管包。
    # --draft-first 更进一步：草稿 Release 不会创建标签，所以标签根本不会提前出现。
    if args.release:
        tag = args.tag or ""
        if not tag:
            die("--release 需要同时给 --tag（Release 需要一个标签名）")
        notes = ""
        if args.notes_file and pathlib.Path(args.notes_file).is_file():
            notes = pathlib.Path(args.notes_file).read_text(encoding="utf-8")
        else:
            notes = f"AudioCpp.NET {tag.lstrip('v')}"
        rel = create_release(owner, name, tag, token, notes, args.prerelease,
                             draft=args.draft_first)
        if args.assets:
            ad = pathlib.Path(args.assets)
            if not ad.is_dir():
                warn(f"附件目录不存在: {ad}")
            else:
                n = upload_assets(owner, name, rel, ad, token)
                info(f"附件处理完成，共 {n} 个")
        if args.draft_first and rel.get("draft"):
            publish_release(owner, name, rel["id"], tag, token)

    if args.tag:
        tags = run(["git", "tag", "--list", args.tag], cwd=repo_dir, check=False).stdout.strip()
        if tags:
            info(f"本地标签已存在: {args.tag}")
        else:
            info(f"创建本地标签 {args.tag}")
            run(["git", "tag", "-a", args.tag, "-m", args.tag], cwd=repo_dir)
        # 发布 Release 时 GitHub 已经建好了远端标签，此时再推会被拒（already exists）。
        # 那是预期结果而不是错误，先查一下远端，避免无意义的失败。
        remote = run(["git", "ls-remote", "--tags", "origin", args.tag],
                     cwd=repo_dir, check=False, quiet=True).stdout.strip()
        if remote:
            info(f"远端标签已存在，跳过推送: {args.tag}")
        else:
            git_push(repo_dir, f"refs/tags/{args.tag}", token, slug)

    info("完成。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
