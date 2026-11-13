#!/usr/bin/env python3
"""Headless end-to-end drills for AudioCpp.NET.Web resumable weight downloads.

The workbench serves its own deterministic Range source, so these tests need no
external network and never download a multi-gigabyte model. Every scenario runs
the *real* background downloader and finishes with a byte-for-byte check.

Usage:
    python eng/Test-WeightResume.py [--base http://127.0.0.1:62029]

Prerequisite: the Web workbench is running (dotnet run --project
src/AudioCpp.NET.Web). The script restores the workbench configuration it
changes even when a scenario fails.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

TERMINAL = {"completed", "failed", "interrupted", "cancelled", "paused"}
SEED = 42


class Drill:
    def __init__(self, base: str):
        self.base = base.rstrip("/")
        self.results: list[tuple[str, bool, str]] = []

    def call(self, method: str, path: str, body=None, timeout: float = 60):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(
            self.base + path,
            data=data,
            method=method,
            headers={"Content-Type": "application/json"} if data else {},
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            detail = error.read().decode(errors="replace")
            raise RuntimeError(f"{method} {path} -> HTTP {error.code}: {detail}") from error

    def wait(self, job_id: str, terminal=TERMINAL, timeout: float = 120, progress=None):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            last = self.call("GET", f"/api/weights/jobs/{job_id}")
            if progress and not progress(last):
                return last
            if last.get("state") in terminal:
                return last
            time.sleep(0.1)
        raise TimeoutError(f"job {job_id} stuck in {last and last.get('state')}")

    def check(self, name: str, condition: bool, detail: str):
        self.results.append((name, bool(condition), detail))
        print(("  PASS  " if condition else "  FAIL  ") + name + " :: " + detail, flush=True)

    @staticmethod
    def first_file(job):
        files = job.get("files") or []
        if not files:
            raise RuntimeError("job has no files")
        return files[0]

    @staticmethod
    def blob(size: int, seed: int = SEED) -> bytes:
        return bytes(((i * 31 + seed * 17 + 7) & 0xFF) for i in range(size))

    def verify_api(self, path: str, size: int):
        query = urllib.parse.urlencode({"path": path, "size": size, "seed": SEED})
        return self.call("GET", "/api/weights/test/verify?" + query)

    def config(self, **kwargs):
        return self.call("POST", "/api/weights/config", kwargs)

    def reset_config(self):
        return self.config(chunkBytes=1048576, maxBytesPerSecond=0, abortAfterBytes=0,
                           abortAtByte=0, maxChunkBytes=0, seed=SEED)

    def start_test(self, **kwargs):
        return self.call("POST", "/api/weights/test/start", kwargs)

    # ---------------------------------------------------------------- scenarios

    def dropped_connection(self):
        """Real TCP cut -> .part kept -> Range resume from the exact byte offset."""
        print("\n[1] dropped connection / Range resume", flush=True)
        self.reset_config()
        self.call("POST", "/api/weights/test/clean")
        size, abort = 8 * 1024 * 1024, 3_000_000
        job = self.start_test(sizeBytes=size, seed=SEED, abortAtByte=abort,
                              maxChunkBytes=256 * 1024, chunkBytes=1024 * 1024)
        first = self.wait(job["jobId"])
        part = self.first_file(first)["path"] + ".part"
        # A dropped connection can lose the last in-flight buffer, so the
        # credible resume offset is the bytes actually persisted on disk.
        offset = os.path.getsize(part)
        self.check("断流后任务失败且保留部分数据",
                   first["state"] in ("failed", "interrupted")
                   and offset > 0 and offset <= abort
                   and first["bytesDownloaded"] == offset,
                   f"state={first['state']} bytes={first['bytesDownloaded']} "
                   f"part={offset} plannedCut={abort}")
        resumed = self.call("POST", f"/api/weights/jobs/{job['jobId']}/resume")
        second = self.wait(job["jobId"])
        file = self.first_file(second)
        verified = self.verify_api(file["path"], size)
        self.check("从断点续传并完成逐字节校验",
                   resumed["message"] == "resuming"
                   and second["state"] == "completed"
                   and file["resumedFrom"] == offset
                   and second["fullRestarts"] == 0
                   and verified.get("valid") is True,
                   f"resumedFrom={file['resumedFrom']} ranges={second['rangeRequests']} "
                   f"restarts={second['fullRestarts']} verify={verified.get('valid')}")

    def pause_race(self):
        """Pause while a chunk is in flight, then resume immediately (no deadlock)."""
        print("\n[2] pause / immediate resume / cancel resume", flush=True)
        self.reset_config()
        self.call("POST", "/api/weights/test/clean")
        self.config(chunkBytes=256 * 1024, maxBytesPerSecond=4_000_000)
        size = 8 * 1024 * 1024

        # (a) Resume before the worker has left its current chunk: StartRun sees
        # Running == true, so Paused must be cleared by Resume itself.
        job = self.start_test(sizeBytes=size, seed=SEED, chunkBytes=256 * 1024)
        self.wait(job["jobId"], progress=lambda j: j.get("bytesDownloaded", 0) < 1024 * 1024,
                  terminal=set())
        paused = self.call("POST", f"/api/weights/jobs/{job['jobId']}/pause")
        resumed = self.call("POST", f"/api/weights/jobs/{job['jobId']}/resume")
        second = self.wait(job["jobId"])
        verified = self.verify_api(self.first_file(second)["path"], size)
        self.check("暂停后立即继续不会死锁且文件无损",
                   paused["state"] == "paused" and second["state"] == "completed"
                   and verified.get("valid") is True,
                   f"pause={paused['state']} resume={resumed['state']} "
                   f"verify={verified.get('valid')}")

        # (b) Cancel stops the worker; resume must re-open the .part file and
        # continue from the byte offset recorded on disk.
        self.call("POST", "/api/weights/test/clean")
        job = self.start_test(sizeBytes=size, seed=SEED, chunkBytes=256 * 1024)
        self.wait(job["jobId"], progress=lambda j: j.get("bytesDownloaded", 0) < 1024 * 1024,
                  terminal=set())
        cancelled = self.call("POST", f"/api/weights/jobs/{job['jobId']}/cancel")
        self.wait(job["jobId"], progress=lambda j: j.get("running") is True, terminal=set())
        self.call("POST", f"/api/weights/jobs/{job['jobId']}/resume")
        final = self.wait(job["jobId"])
        file = self.first_file(final)
        verified = self.verify_api(file["path"], size)
        self.check("取消后重新读取磁盘分片续传",
                   cancelled["state"] == "cancelled" and final["state"] == "completed"
                   and file["resumedFrom"] > 0 and final["resumeCount"] >= 1
                   and verified.get("valid") is True,
                   f"resumedFrom={file['resumedFrom']} resumes={final['resumeCount']} "
                   f"verify={verified.get('valid')}")

    def injected_interrupt(self):
        """Client-side interrupt injection -> 'interrupted' -> resume with new options."""
        print("\n[3] client-side injected interrupt", flush=True)
        self.reset_config()
        self.call("POST", "/api/weights/test/clean")
        self.config(abortAfterBytes=1_234_567)
        size = 2 * 1024 * 1024
        job = self.start_test(sizeBytes=size, seed=SEED, chunkBytes=256 * 1024)
        first = self.wait(job["jobId"])
        part = self.first_file(first)["path"] + ".part"
        offset = os.path.getsize(part)
        self.reset_config()  # the resumed run must not be interrupted again
        self.call("POST", f"/api/weights/jobs/{job['jobId']}/resume")
        second = self.wait(job["jobId"])
        file = self.first_file(second)
        verified = self.verify_api(file["path"], size)
        self.check("注入中断后可用新配置继续下载",
                   first["state"] == "interrupted" and offset >= 1_234_567
                   and second["state"] == "completed"
                   and file["resumedFrom"] == offset
                   and verified.get("valid") is True,
                   f"state={first['state']} offset={offset} "
                   f"resumedFrom={file['resumedFrom']} verify={verified.get('valid')}")

    def server_ignores_range(self):
        """Server answers a resume with a full 200: discard .part, never splice."""
        print("\n[4] server ignores Range on resume", flush=True)
        self.reset_config()
        self.call("POST", "/api/weights/test/clean")
        size, abort = 2 * 1024 * 1024, 700_000
        job = self.start_test(sizeBytes=size, seed=SEED, abortAtByte=abort,
                              chunkBytes=size, noRanges=True)
        first = self.wait(job["jobId"])
        self.call("POST", f"/api/weights/jobs/{job['jobId']}/resume")
        second = self.wait(job["jobId"])
        file = self.first_file(second)
        verified = self.verify_api(file["path"], size)
        self.check("200 全量响应触发安全重启而非错拼",
                   first["state"] in ("failed", "interrupted")
                   and second["state"] == "completed"
                   and second["fullRestarts"] >= 1
                   and verified.get("valid") is True,
                   f"first={first['state']} final={second['state']} "
                   f"restarts={second['fullRestarts']} "
                   f"verify={verified.get('valid')} reason={verified.get('reason')}")

    def tampered_metadata(self):
        """A changed ETag invalidates the sidecar: restart from zero, no corruption."""
        print("\n[5] tampered .part.meta", flush=True)
        self.reset_config()
        self.call("POST", "/api/weights/test/clean")
        size = 2 * 1024 * 1024
        job = self.start_test(sizeBytes=size, seed=SEED, abortAtByte=500_000,
                              chunkBytes=1024 * 1024)
        first = self.wait(job["jobId"])
        file = self.first_file(first)
        meta_path = file["path"] + ".part.meta"
        with open(meta_path, "r", encoding="utf-8") as handle:
            meta = json.load(handle)
        meta["ETag"] = '"stale-object"'
        with open(meta_path, "w", encoding="utf-8") as handle:
            json.dump(meta, handle)
        self.call("POST", f"/api/weights/jobs/{job['jobId']}/resume")
        second = self.wait(job["jobId"])
        verified = self.verify_api(self.first_file(second)["path"], size)
        self.check("ETag 变化时丢弃旧分片重新下载",
                   first["state"] in ("failed", "interrupted")
                   and second["state"] == "completed"
                   and self.first_file(second)["resumedFrom"] == 0
                   and verified.get("valid") is True,
                   f"resumedFrom={self.first_file(second)['resumedFrom']} "
                   f"final={second['state']} verify={verified.get('valid')} "
                   f"reason={verified.get('reason')}")

    def real_package(self, repo_root: str):
        """Point the endpoint at the local test source and download a real
        catalog package: verifies URL construction, strip_prefix and target path."""
        print("\n[6] real package id -> local Range source", flush=True)
        self.reset_config()
        original = self.call("GET", "/api/config")
        value = lambda key, pascal: original.get(key, original.get(pascal, ""))
        scratch = os.path.join(repo_root, "build", "logs", "weight-e2e-models")
        try:
            catalog = self.call("GET", "/api/weights/packages")
            package = next(p for p in catalog["packages"] if p["id"] == "ace_step_base_bf16")
            self.call("POST", "/api/config", {
                "nativePath": "",
                "modelsDirectory": scratch,
                "huggingFaceEndpoint": self.base + "/api/weights/test-source",
            })
            job = self.call("POST", "/api/weights/download",
                            {"packageId": "ace_step_base_bf16", "overwrite": True})
            done = self.wait(job["jobId"], timeout=180)
            file = self.first_file(done)
            data = open(file["path"], "rb").read() if os.path.exists(file["path"]) else b""
            expected = self.blob(len(data))
            self.check("按 package id 解析 URL 并落地到目标目录",
                       done["state"] == "completed"
                       and len(data) == 4 * 1024 * 1024
                       and data == expected
                       and file["url"].startswith(self.base + "/api/weights/test-source/")
                       and file["path"].lower().startswith(scratch.lower()),
                       f"state={done['state']} ranges={done['rangeRequests']} "
                       f"bytes={len(data)} files={len(package['files'])} "
                       f"path={os.path.basename(file['path'])}")
        finally:
            self.call("POST", "/api/config", {
                "nativePath": value("nativePath", "NativePath"),
                "modelsDirectory": value("modelsDirectory", "ModelsDirectory"),
                "huggingFaceEndpoint": value("huggingFaceEndpoint", "HuggingFaceEndpoint"),
            })
            shutil.rmtree(scratch, ignore_errors=True)

    def run(self, repo_root: str) -> int:
        self.wait_ready()
        try:
            self.reset_config()
            self.dropped_connection()
            self.pause_race()
            self.injected_interrupt()
            self.server_ignores_range()
            self.tampered_metadata()
            self.real_package(repo_root)
        finally:
            self.reset_config()
            self.call("POST", "/api/weights/test/clean")

        print("\n==== summary ====", flush=True)
        for name, ok, detail in self.results:
            print(f"  {'PASS' if ok else 'FAIL'}  {name}: {detail}")
        failed = [name for name, ok, _ in self.results if not ok]
        print(f"\n{len(self.results) - len(failed)}/{len(self.results)} checks passed")
        return 1 if failed else 0

    def wait_ready(self):
        deadline = time.time() + 30
        while time.time() < deadline:
            try:
                self.call("GET", "/api/weights/config", timeout=2)
                return
            except Exception:
                time.sleep(0.5)
        raise RuntimeError(f"workbench is not reachable at {self.base}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:62029")
    parser.add_argument("--repo-root", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    args = parser.parse_args()
    sys.exit(Drill(args.base).run(args.repo_root))
