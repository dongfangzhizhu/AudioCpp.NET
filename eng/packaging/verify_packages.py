#!/usr/bin/env python3
"""Assert the built packages actually contain what consumers need.

A nupkg that builds is not the same as a nupkg that works. An asset package with no
runtimes/ entry, or a managed package missing the interop assembly, publishes fine and
then fails on the consumer's first run with a DllNotFoundException.

Written in plain Python rather than shell on purpose: it has to run identically from
the Windows packaging script, from a Linux CI job, and by hand. Two shell dialects (plus
a blocked bash-from-PowerShell path) would be three chances to disagree about what a
correct package looks like.

Usage:
    python eng/packaging/verify_packages.py <packages-dir> [--natives staged|skipped]
                                            [--rids win-x64,linux-x64]
"""

from __future__ import annotations

import argparse
import re
import sys
import zipfile
from pathlib import Path

MANAGED_REQUIRED = [
    "lib/net10.0/AudioCpp.NET.dll",
    "lib/net10.0/AudioCpp.NET.Interop.dll",
    "README.md",
]

# The native file each RID contributes. A runtime package is expected to carry every
# RID the release was built for -- see --rids -- and each one has its own file name.
NATIVE_FILE = {
    "win-x64": "runtimes/win-x64/native/audiocpp_dotnet_native.dll",
    "linux-x64": "runtimes/linux-x64/native/libaudiocpp_dotnet_native.so",
}

RUNTIME_REQUIRED = ["README-runtime.md"]

# Every runtime asset package that may appear in a release, in the order a reader
# should pick one. --backends narrows this to the set a given release actually
# staged; the default keeps verifying all three so a half-staged backend set is
# caught instead of silently shipped.
RUNTIME_PACKAGE_IDS = [
    "AudioCpp.NET.Runtime",
    "AudioCpp.NET.Runtime.Cuda",
    "AudioCpp.NET.Runtime.Vulkan",
]

BACKEND_TO_PACKAGE_ID = {
    "cpu": "AudioCpp.NET.Runtime",
    "cuda": "AudioCpp.NET.Runtime.Cuda",
    "vulkan": "AudioCpp.NET.Runtime.Vulkan",
}

VERSION = r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?"


class Report:
    def __init__(self) -> None:
        self.failures = 0

    def ok(self, message: str) -> None:
        print(f"  + {message}")

    def bad(self, message: str) -> None:
        print(f"  ! {message}")
        self.failures += 1


def pick(packages: Path, package_id: str, extension: str) -> Path | None:
    """Find the one file named exactly <package_id>.<version>.<extension>.

    Matching on the literal "<package_id>." prefix followed by a version is what
    keeps "AudioCpp.NET" from also selecting "AudioCpp.NET.Runtime.0.1.0.nupkg":
    the character after the prefix must begin a version, not continue the ID.
    """
    pattern = re.compile(
        rf"^{re.escape(package_id)}\.{VERSION}\.{re.escape(extension)}$"
    )
    matches = sorted(p for p in packages.iterdir() if pattern.match(p.name))
    return matches[0] if matches else None


def entries(path: Path) -> set[str]:
    with zipfile.ZipFile(path) as archive:
        return set(archive.namelist())


def nuspec(path: Path) -> str:
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            if name.endswith(".nuspec"):
                return archive.read(name).decode("utf-8", "replace")
    return ""


def verify_managed(packages: Path, report: Report) -> None:
    nupkg = pick(packages, "AudioCpp.NET", "nupkg")
    if nupkg is None:
        report.bad("no AudioCpp.NET.<version>.nupkg found")
        return

    print(nupkg.name)
    contents = entries(nupkg)
    for required in MANAGED_REQUIRED:
        (report.ok if required in contents else report.bad)(
            required if required in contents else f"missing {required}"
        )

    # The interop layer must be *inside* the package. If it were merely a dependency
    # the package would still build, but the dependency does not exist on the feed.
    spec = nuspec(nupkg)
    if "AudioCpp.NET.Interop" in spec and "<dependency" in spec and "Interop" in spec.split("<dependencies")[-1]:
        report.bad("declares a dependency on the unpublished interop assembly")
    else:
        report.ok("interop embedded, not a dangling dependency")

    snupkg = pick(packages, "AudioCpp.NET", "snupkg")
    if snupkg is not None:
        report.ok(f"symbols package: {snupkg.name}")
    else:
        report.bad("no managed symbols package")


def verify_runtime(packages: Path, package_id: str, rids: list[str], report: Report) -> None:
    nupkg = pick(packages, package_id, "nupkg")
    if nupkg is None:
        report.bad(f"no {package_id}.<version>.nupkg found")
        return

    print(nupkg.name)
    contents = entries(nupkg)
    required = RUNTIME_REQUIRED + [NATIVE_FILE[rid] for rid in rids] + [
        f"buildTransitive/{package_id}.props",
        f"buildTransitive/{package_id}.targets",
    ]
    for entry in required:
        (report.ok if entry in contents else report.bad)(
            entry if entry in contents else f"missing {entry}"
        )

    # The reverse direction matters as much: a native file for a RID the release was
    # not built for is an archive that got carried over. Accepting it would put a
    # library of unknown provenance in the package.
    for rid, entry in NATIVE_FILE.items():
        if rid not in rids and entry in contents:
            report.bad(f"carries {entry}, which is not one of the staged RIDs ({','.join(rids)})")

    # Asset-only packages must stay dependency-free: the consumer adds the managed
    # reference explicitly, and a dependency here would silently pull it in.
    if "<dependency" in nuspec(nupkg):
        report.bad("declares package dependencies (expected none)")
    else:
        report.ok("no package dependencies")

    if any(n.endswith(".snupkg") for n in contents):
        report.bad("contains symbols (unexpected for an asset-only package)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path, help="directory holding the built packages")
    parser.add_argument("--natives", choices=["staged", "skipped"], default="staged",
                        help="whether native archives were available for this build")
    parser.add_argument("--rids", default="win-x64,linux-x64",
                        help="comma-separated RIDs the runtime packages are expected to carry "
                             "(default: both; pass what was actually staged)")
    parser.add_argument("--backends", default="",
                        help="comma-separated backends this release packaged, e.g. cpu,vulkan "
                             "(default: verify every runtime package; pass the set a release "
                             "actually staged)")
    args = parser.parse_args()

    rids = [r.strip() for r in args.rids.split(",") if r.strip()]
    unknown = [r for r in rids if r not in NATIVE_FILE]
    if unknown:
        print(f"FATAL: unknown RID(s): {', '.join(unknown)}", file=sys.stderr)
        return 2

    package_ids = list(RUNTIME_PACKAGE_IDS)
    if args.backends:
        backends = [b.strip() for b in args.backends.split(",") if b.strip()]
        unknown = [b for b in backends if b not in BACKEND_TO_PACKAGE_ID]
        if unknown:
            print(f"FATAL: unknown backend(s): {', '.join(unknown)}", file=sys.stderr)
            return 2
        package_ids = [BACKEND_TO_PACKAGE_ID[b] for b in backends]
    if args.natives == "staged" and not rids:
        print("FATAL: --natives staged but --rids is empty", file=sys.stderr)
        return 2

    if not args.packages.is_dir():
        print(f"FATAL: not a directory: {args.packages}", file=sys.stderr)
        return 2

    print(f"verifying packages in {args.packages}")
    report = Report()

    verify_managed(args.packages, report)

    if args.natives == "skipped":
        print()
        print("runtime packages: skipped (no native archives for this release)")
    else:
        print()
        print(f"runtime packages: expecting RIDs {', '.join(rids)}")
        for package_id in package_ids:
            verify_runtime(args.packages, package_id, rids, report)

    print()
    if report.failures:
        print(f"PACKAGE VERIFICATION FAILED ({report.failures} problem(s))")
        return 1
    print("PACKAGE VERIFICATION OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
