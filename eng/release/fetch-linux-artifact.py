#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""下载 native-archives-linux.yml run 的 artifact，解出内层 linux cuda 归档。

用法: python eng/release/fetch-linux-artifact.py <run_id> <backend> <out_dir>
产物: <out_dir>/audiocpp-native-linux-x64-<backend>.zip（内层归档）
"""
import json
import pathlib
import sys
import time
import urllib.request
import zipfile

TOKEN = pathlib.Path("D:/SouceCode/python2net/audio/audiocpp-dotnet/build/.github-token").read_text().strip()
REPO = "dongfangzhizhu/AudioCpp.NET"


class NoAuthRedirect(urllib.request.HTTPRedirectHandler):
    """跨主机重定向（GitHub → Azure blob）时剥离 Authorization 头。"""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None:
            new.headers.pop("Authorization", None)
        return new


opener = urllib.request.build_opener(NoAuthRedirect)


def api(path, retries=8):
    for i in range(retries):
        try:
            req = urllib.request.Request("https://api.github.com" + path, headers={
                "Accept": "application/vnd.github+json",
                "Authorization": "Bearer " + TOKEN,
                "User-Agent": "audiocpp-release"})
            with opener.open(req, timeout=60) as r:
                return json.load(r)
        except Exception:
            if i == retries - 1:
                raise
            time.sleep(10)


def download(url, dest, retries=8):
    for i in range(retries):
        try:
            req = urllib.request.Request(url, headers={
                "Accept": "application/vnd.github+json",
                "Authorization": "Bearer " + TOKEN,
                "User-Agent": "audiocpp-release"})
            with opener.open(req, timeout=1800) as r:
                data = r.read()
            pathlib.Path(dest).write_bytes(data)
            return len(data)
        except Exception as e:
            print(f"  download attempt {i+1} failed: {e!r}", flush=True)
            if i == retries - 1:
                raise
            time.sleep(15)


def main():
    run_id, backend, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
    out = pathlib.Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)

    arts = api(f"/repos/{REPO}/actions/runs/{run_id}/artifacts")
    cand = [a for a in arts["artifacts"] if backend in a["name"]]
    if len(cand) != 1:
        print(f"FATAL: expected 1 artifact matching '{backend}', got {[a['name'] for a in cand]}")
        sys.exit(1)
    art = cand[0]
    print(f"artifact: {art['name']}  {art['size_in_bytes']:,} B  expired={art['expired']}")

    outer = out / f"artifact-{backend}.zip"
    n = download(art["archive_download_url"], outer)
    print(f"downloaded outer zip: {n:,} B -> {outer}")

    # 解出内层 audiocpp-native-linux-x64-<backend>.zip
    with zipfile.ZipFile(outer) as z:
        names = z.namelist()
        print("outer entries:", names)
        inner_name = f"audiocpp-native-linux-x64-{backend}.zip"
        if inner_name not in names:
            # 有些 CI 直接把 .so 或别的结构打进去，列出全部再判
            print(f"FATAL: {inner_name} not in artifact; entries above")
            sys.exit(1)
        data = z.read(inner_name)
    inner = out / inner_name
    inner.write_bytes(data)
    print(f"inner archive: {inner}  {len(data):,} B")

    # 验证内嵌 pin
    with zipfile.ZipFile(inner) as z:
        so = [n for n in z.namelist() if n.endswith(".so")]
        if len(so) != 1:
            print(f"FATAL: expected 1 .so, got {so}")
            sys.exit(1)
        so_bytes = z.read(so[0])
    pin = b"22cad6af5043a2d7c7f1d9a0e6c9d7996a80dfc8"
    print(f"so: {so[0]}  {len(so_bytes):,} B  embedded pin: {pin in so_bytes}")
    if pin not in so_bytes:
        print("FATAL: embedded pin mismatch")
        sys.exit(1)
    print("OK")


if __name__ == "__main__":
    main()
