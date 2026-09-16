#!/usr/bin/env bash
# Assert the packages produced by a release actually contain what consumers need.
#
# A nupkg that builds is not the same as a nupkg that works: an asset package with no
# runtimes/ entry, or a managed package missing the interop assembly, is a publish-time
# success and a first-run DllNotFoundException. This checks the layout before push.
#
# Usage:
#   eng/packaging/verify-packages.sh <packages-dir> [natives-staged: success|skipped]
set -euo pipefail

DIR=${1:?usage: verify-packages.sh <packages-dir> [success|skipped]}
NATIVES=${2:-success}

fail=0
note() { echo "  ! $*"; fail=1; }
ok()   { echo "  + $*"; }

list() { python3 -c "
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    for n in sorted(z.namelist()):
        print(n)
" "$1"; }

echo "verifying packages in $DIR"

# --- managed package ----------------------------------------------------------
managed=$(ls "$DIR"/AudioCpp.NET.[0-9]*.nupkg 2>/dev/null | grep -v Runtime | head -1 || true)
if [ -z "$managed" ]; then
  echo "FATAL: no AudioCpp.NET.<version>.nupkg found" >&2
  exit 2
fi

echo "$(basename "$managed")"
contents=$(list "$managed")
for required in \
    'lib/net10.0/AudioCpp.NET.dll' \
    'lib/net10.0/AudioCpp.NET.Interop.dll' \
    'README.md' ; do
  if printf '%s\n' "$contents" | grep -qx "$required"; then ok "$required"; else note "missing $required"; fi
done
# The interop assembly must be inside the package, not a dangling package dependency:
# it is implementation detail and is not published separately.
if printf '%s\n' "$contents" | grep -q 'lib/net10.0/AudioCpp.NET.Interop.xml'; then
  ok "interop xml docs present"
fi

# --- runtime packages ---------------------------------------------------------
if [ "$NATIVES" != "success" ]; then
  echo
  echo "runtime packages: skipped (no native archives for this release)"
else
  for pkg in AudioCpp.NET.Runtime AudioCpp.NET.Runtime.Cuda; do
    nupkg=$(ls "$DIR/$pkg".[0-9]*.nupkg 2>/dev/null | head -1 || true)
    if [ -z "$nupkg" ]; then note "no $pkg.<version>.nupkg found"; continue; fi
    echo "$(basename "$nupkg")"
    contents=$(list "$nupkg")
    for required in \
        'runtimes/win-x64/native/audiocpp_dotnet_native.dll' \
        'runtimes/linux-x64/native/libaudiocpp_dotnet_native.so' \
        "buildTransitive/$pkg.props" \
        "buildTransitive/$pkg.targets" \
        'README-runtime.md' ; do
      if printf '%s\n' "$contents" | grep -qx "$required"; then ok "$required"; else note "missing $required"; fi
    done
    # A runtime package must not drag the managed package in as a dependency: it
    # supplies assets only, and the consumer adds the managed reference explicitly.
    nuspec=$(python3 -c "
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    for n in z.namelist():
        if n.endswith('.nuspec'):
            sys.stdout.write(z.read(n).decode('utf-8', 'replace'))
            break
" "$nupkg")
    if printf '%s' "$nuspec" | grep -q '<dependency'; then
      note "declares package dependencies (expected none)"
    else
      ok "no package dependencies"
    fi
  done
fi

# --- symbols ------------------------------------------------------------------
snupkg=$(ls "$DIR"/AudioCpp.NET.[0-9]*.snupkg 2>/dev/null | grep -v Runtime | head -1 || true)
if [ -n "$snupkg" ]; then ok "symbols package: $(basename "$snupkg")"; else note "no managed symbols package"; fi

echo
if [ "$fail" -ne 0 ]; then
  echo "PACKAGE VERIFICATION FAILED"
  exit 1
fi
echo "PACKAGE VERIFICATION OK"
