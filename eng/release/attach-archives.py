#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 native archives（win 三后端 + linux cpu）挂到 Release 上，并生成合并后的 manifest。

背景
----
release.yml 的 pack job 从 GitHub Release（与 tag 同名）下载 audiocpp-native-*.zip 和
audiocpp-native-manifest.json。linux archives 由 native-archives-linux.yml 的 CI 构建
（本次 CI 的 attach 步骤失败，archive 改从 workflow artifact 手动取回）；win archives 在
本机构建。两者都由本脚本挂到 release 上。

manifest 合并规则
-----------------
manifest 是全部 archives 的 provenance 记录（pin + ABI + 每库 SHA256）。本脚本：
  1. 读取本地 manifest（win 条目，由 make-native-archives.ps1 生成）；
  2. 若给了 --linux-zip，解出 libaudiocpp_dotnet_native.so 计算哈希，生成 linux 条目；
  3. 与 release 上已有的 manifest 合并（同 rid+backend 以本地/新计算值为准）；
  4. 删除 release 上的旧 manifest，上传合并后的；
  5. 上传全部 zip（同名跳过）。

用法::

    GITHUB_TOKEN=xxx python eng/release/attach-archives.py \
        --repo dongfangzhizhu/AudioCpp.NET --tag v0.4.0 \
        --archives build/native-archives \
        --linux-zip build/artifacts/linux-cpu-artifact.zip

    # 只看会做什么
    ... --dry-run
"""

from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import os
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"
UA = "audiocpp-dotnet-attach"
DRY = False


def info(m: str) -> None:
    print(f":: {m}", flush=True)


def die(m: str, code: int = 1) -> None:
    print(f"!!! {m}", file=sys.stderr, flush=True)
    sys.exit(code)


def get_token() -> str:
    tok = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if tok:
        return tok
    p = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, encoding="utf-8",
    )
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    die("没有 GITHUB_TOKEN/GH_TOKEN，且 git credential fill 也没取到令牌")


def api(token: str, method: str, path: str, body=None, raw: bool = False,
        accept: str = "application/vnd.github+json", retries: int = 6):
    url = path if path.startswith("http") else API + path
    data = json.dumps(body).encode() if body is not None else None
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": accept,
        "User-Agent": UA,
    }
    if data is not None:
        headers["Content-Type"] = "application/json"
    last = None
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, data=data, headers=headers, method=method)
            with urllib.request.urlopen(req, timeout=60) as r:
                payload = r.read()
                return r.status, (payload if raw else json.loads(payload or b"null"))
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code}: {e.read()[:300]!r}"
            if e.code in (404, 422) and method != "GET":
                return e.code, None
            if e.code == 403 and "rate limit" in str(last).lower():
                time.sleep(5 * (attempt + 1))
                continue
        except Exception as e:
            last = repr(e)
        time.sleep(2 * (attempt + 1))
    die(f"API {method} {path} 重试 {retries} 次仍失败: {last}")


def sha256_file(p: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def linux_entry_from_zip(zip_path: pathlib.Path, backend: str = "cpu") -> dict:
    """从 CI artifact zip 里解出 .so，计算 manifest 条目（与 CI 的 pack 步骤同格式）。"""
    with zipfile.ZipFile(zip_path) as z:
        names = [n for n in z.namelist() if n.endswith(".so")]
        if len(names) != 1:
            die(f"{zip_path} 里应有且仅有 1 个 .so，实际: {names}")
        so_bytes = z.read(names[0])
    so_name = names[0]
    rid = "linux-x64"
    entry = {
        "rid": rid,
        "backend": backend,
        "archive": f"audiocpp-native-{rid}-{backend}.zip",
        "library": so_name,
        "library_bytes": len(so_bytes),
        "library_sha256": hashlib.sha256(so_bytes).hexdigest(),
        "archive_sha256": sha256_file(zip_path),
        "archive_bytes": zip_path.stat().st_size,
    }
    return entry


def main() -> int:
    global DRY
    ap = argparse.ArgumentParser(description="挂 native archives 到 Release 并合并 manifest")
    ap.add_argument("--repo", required=True, help="owner/name")
    ap.add_argument("--tag", required=True, help="Release tag，如 v0.4.0")
    ap.add_argument("--archives", required=True, help="win archives 目录（含 zip 与 manifest）")
    ap.add_argument("--linux-zip", default="", help="linux archive zip（CI artifact）")
    ap.add_argument("--linux-backend", default="cpu", help="linux zip 对应的后端（cpu/cuda/vulkan）")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    DRY = args.dry_run

    token = get_token()
    owner, name = args.repo.split("/", 1)
    adir = pathlib.Path(args.archives).resolve()

    # ---- 找 release（list 端点才看得到 draft） ----
    st, rels = api(token, "GET", f"/repos/{owner}/{name}/releases?per_page=100")
    rel = next((r for r in rels if r["tag_name"] == args.tag), None)
    if rel is None:
        die(f"找不到 tag={args.tag} 的 Release（含 draft）")
    info(f"release: id={rel['id']} draft={rel['draft']} assets={len(rel.get('assets', []))}")

    # ---- 下载 release 上现有 manifest（可能已有 CI 写入的条目） ----
    existing_assets = {a["name"]: a for a in rel.get("assets", [])}
    merged_entries: list[dict] = []
    man_asset = existing_assets.get("audiocpp-native-manifest.json")
    if man_asset is not None:
        # 注意：release asset 下载端点必须 Accept: application/octet-stream，
        # 否则返回的是资产元数据 JSON（无 archives 键），会被误读成 0 条目。
        st, blob = api(token, "GET",
                       f"/repos/{owner}/{name}/releases/assets/{man_asset['id']}",
                       raw=True, accept="application/octet-stream")
        try:
            old = json.loads(blob)
            merged_entries = list(old.get("archives", []))
            info(f"现有 manifest：pin={old.get('audioCppCommit', '?')[:12]} "
                 f"条目 {len(merged_entries)} 个")
        except Exception as e:
            die(f"现有 manifest 解析失败: {e}")

    # ---- 本地 win 条目 ----
    local_manifest = adir / "audiocpp-native-manifest.json"
    if not local_manifest.exists():
        die(f"缺少 {local_manifest}")
    local = json.loads(local_manifest.read_text(encoding="utf-8"))
    local_entries = list(local.get("archives", []))
    if not local_entries:
        die("本地 manifest 里没有条目")

    # ---- linux 条目（可选） ----
    linux_zip = pathlib.Path(args.linux_zip).resolve() if args.linux_zip else None
    if linux_zip is not None:
        if not linux_zip.exists():
            die(f"linux zip 不存在: {linux_zip}")
        le = linux_entry_from_zip(linux_zip, args.linux_backend)
        local_entries.append(le)
        info(f"linux 条目: {le['archive']}  lib={le['library_bytes']:,} B  "
             f"sha256={le['library_sha256'][:16]}…")

    # ---- 合并（同 rid+backend 以新值为准） ----
    for e in local_entries:
        k = (e["rid"], e["backend"])
        merged_entries = [x for x in merged_entries if (x["rid"], x["backend"]) != k]
        merged_entries.append(e)
    merged = dict(local)
    merged["archives"] = merged_entries
    merged["builtAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + f".{time.time_ns() % 1_000_000_000:09d}Z"
    info(f"合并后 manifest：pin={merged.get('audioCppCommit', '?')[:12]} "
         f"条目 {len(merged_entries)} 个: "
         + ", ".join(f"{e['rid']}/{e['backend']}" for e in merged_entries))

    # ---- 收集要上传的文件 ----
    to_upload: list[tuple[pathlib.Path, str]] = []
    for e in local_entries:
        if e["rid"] == "linux-x64" and linux_zip is not None:
            to_upload.append((linux_zip, e["archive"]))
        else:
            f = adir / e["archive"]
            if not f.exists():
                die(f"manifest 引用了不存在的文件: {f}")
            to_upload.append((f, e["archive"]))

    for f, aname in to_upload:
        if aname in existing_assets:
            info(f"附件已存在，跳过: {aname}")
            continue
        if DRY:
            info(f"DRY-RUN 会上传 {aname} ({f.stat().st_size:,} B)")
            continue
        _upload(token, owner, name, rel["id"], f, override_name=aname)
        info(f"已上传 {aname}")

    # ---- 替换 manifest（删旧传新） ----
    man_bytes = json.dumps(merged, indent=4, ensure_ascii=False).encode("utf-8")
    if DRY:
        info(f"DRY-RUN 会替换 manifest（{len(man_bytes):,} B）")
        return 0
    if man_asset is not None:
        st, _ = api(token, "DELETE",
                    f"/repos/{owner}/{name}/releases/assets/{man_asset['id']}")
        info(f"删除旧 manifest asset（HTTP {st}）")
    tmp = adir / ".manifest-merged.json"
    tmp.write_bytes(man_bytes)
    _upload(token, owner, name, rel["id"], tmp, override_name="audiocpp-native-manifest.json")
    tmp.unlink()
    info("manifest 已更新")
    return 0


def _upload(token: str, owner: str, name: str, release_id: int,
            path: pathlib.Path, override_name: str = "") -> None:
    fname = override_name or path.name
    ctype = mimetypes.guess_type(fname)[0] or "application/octet-stream"
    url = f"{UPLOADS}/repos/{owner}/{name}/releases/{release_id}/assets?name={fname}"
    data = path.read_bytes()
    req = urllib.request.Request(
        url, data=data, method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "User-Agent": UA,
            "Accept": "application/vnd.github+json",
            "Content-Type": ctype,
            "Content-Length": str(len(data)),
        },
    )
    for attempt in range(6):
        try:
            with urllib.request.urlopen(req, timeout=600) as r:
                if r.status not in (201, 200):
                    die(f"上传 {fname} 失败 HTTP {r.status}")
                return
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", "replace")
            if e.code == 422 and "already_exists" in body:
                info(f"{fname} 已存在（422 already_exists），视为成功")
                return
            if attempt == 5:
                die(f"上传 {fname} 失败: HTTP {e.code} {body[:200]}")
        except Exception:
            if attempt == 5:
                raise
        time.sleep(3 * (attempt + 1))


if __name__ == "__main__":
    sys.exit(main())
