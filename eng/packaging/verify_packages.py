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

RUNTIME_REQUIRED = [
    "runtimes/win-x64/native/audiocpp_dotnet_native.dll",
    "runtimes/linux-x64/native/libaudiocpp_dotnet_native.so",
    "README-runtime.md",
]

VERSIONED = re.compile(r"\.\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\.(nupkg|snupkg)$")


class Report:
    def __init__(self) -> None:
        self.failures = 0

    def ok(self, message: str) -> None:
        print(f"  + {message}")

    def bad(self, message: str) -> None:
        print(f"  ! {message}")
        self.failures += 1


def pick(packages: Path, package_id: str, extension: str) -> Path | None:
    """Find <package_id>.<version>.<ext>, ignoring the runtime packages' own IDs."""
    candidates = [
        p for p in packages.glob(f"{package_id}.*.{extension}")
        if VERSIONED.search(p.name)
    ]
    # AudioCpp.NET.Runtime* also starts with "AudioCpp.NET."; require an exact prefix
    # boundary so the managed package is never confused with a runtime package.
    exact = [p for p in candidates if p.name.split(".", 1)[0] + "." + p.name.split(".", 1)[1].split(".")[0] == package_id]
    return sorted(exact or candidates)[0] if (exact or candidates) else None


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


def verify_runtime(packages: Path, package_id: str, report: Report) -> None:
    nupkg = pick(packages, package_id, "nupkg")
    if nupkg is None:
        report.bad(f"no {package_id}.<version>.nupkg found")
        return

    print(nupkg.name)
    contents = entries(nupkg)
    required = RUNTIME_REQUIRED + [
        f"buildTransitive/{package_id}.props",
        f"buildTransitive/{package_id}.targets",
    ]
    for entry in required:
        (report.ok if entry in contents else report.bad)(
            entry if entry in contents else f"missing {entry}"
        )

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
    args = parser.parse_args()

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
        for package_id in ("AudioCpp.NET.Runtime", "AudioCpp.NET.Runtime.Cuda"):
            verify_runtime(args.packages, package_id, report)

    print()
    if report.failures:
        print(f"PACKAGE VERIFICATION FAILED ({report.failures} problem(s))")
        return 1
    print("PACKAGE VERIFICATION OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
