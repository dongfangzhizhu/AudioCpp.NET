#!/usr/bin/env bash
# Linux/macOS half of the packaging step. Windows counterpart: eng/packaging/pack.ps1.
#
# Stages whichever native shims this machine can see into build/nuget-staging/<backend>/<rid>/
# and packs every publishable project. Staging is additive per RID: running the Windows
# script and then this one on a checkout shared over /mnt/d yields packages carrying both
# win-x64 and linux-x64 assets, which is also how the release workflow composes them from
# separate CI jobs.
#
# Usage:
#   eng/packaging/pack.sh                      # both runtimes, version from Directory.Build.props
#   eng/packaging/pack.sh --version 0.2.0
#   eng/packaging/pack.sh --backends cpu
set -uo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
OUT="$REPO/build/nuget"
STAGING="$REPO/build/nuget-staging"
WSL_MATRIX_ROOT=${AUDIOCPP_MATRIX_ROOT:-$HOME/audio-matrix}
VERSION=""
BACKENDS=(cpu cuda)
SKIP_NATIVE=0

while [ $# -gt 0 ]; do
  case "$1" in
    --version)    VERSION=${2:?}; shift 2 ;;
    --backends)   read -r -a BACKENDS <<< "${2//,/ }"; shift 2 ;;
    --output)     OUT=${2:?}; shift 2 ;;
    --skip-native) SKIP_NATIVE=1; shift ;;
    -h|--help)    sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT"

stage_native() {
  local backend=$1 dest="$STAGING/$1" name=libaudiocpp_dotnet_native.so
  mkdir -p "$dest/linux-x64"

  # Prefer the shim the matrix already copied into the repo, then the build tree.
  local candidate=""
  for c in "$REPO/build/artifacts/matrix-out/linux-$backend/$name" \
           "$WSL_MATRIX_ROOT/build-linux-$backend/$name"; do
    [ -f "$c" ] && { candidate=$c; break; }
  done

  if [ -n "$candidate" ]; then
    cp "$candidate" "$dest/linux-x64/"
    printf '  linux-x64 : %s MiB (%s)\n' "$(awk -v b="$(stat -c%s "$candidate")" 'BEGIN{printf "%.1f", b/1048576}')" "$candidate"
  else
    rmdir "$dest/linux-x64" 2>/dev/null
    echo "  linux-x64 : (absent - build it with eng/matrix/linux-full-matrix.sh)"
  fi

  # win-x64 is staged by pack.ps1; report it so a partial package is obvious.
  if [ -f "$dest/win-x64/audiocpp_dotnet_native.dll" ]; then
    printf '  win-x64   : %s MiB (pre-staged)\n' "$(awk -v b="$(stat -c%s "$dest/win-x64/audiocpp_dotnet_native.dll")" 'BEGIN{printf "%.1f", b/1048576}')"
  else
    echo "  win-x64   : (absent - run eng/packaging/pack.ps1 on Windows)"
  fi
}

if [ "$SKIP_NATIVE" -eq 0 ]; then
  for backend in "${BACKENDS[@]}"; do
    echo "staging native shims: $backend"
    stage_native "$backend"
  done
fi

failed=0
projects=("$REPO/src/AudioCpp.NET/AudioCpp.NET.csproj")
for backend in "${BACKENDS[@]}"; do
  if [ "$backend" = cuda ]; then
    projects+=("$REPO/src/AudioCpp.NET.Runtime.Cuda/AudioCpp.NET.Runtime.Cuda.csproj")
  else
    projects+=("$REPO/src/AudioCpp.NET.Runtime/AudioCpp.NET.Runtime.csproj")
  fi
done

for project in "${projects[@]}"; do
  echo
  echo "=== pack $(basename "$(dirname "$project")") ==="
  cmd=(dotnet pack "$project" -c Release -o "$OUT")
  [ -n "$VERSION" ] && cmd+=("-p:Version=$VERSION")
  "${cmd[@]}" || { echo "!!! PACK FAILED: $project"; failed=$((failed + 1)); }
done

echo
echo "=== verifying package layout ==="
if command -v python3 >/dev/null 2>&1; then PY=python3; else PY=python; fi
if command -v "$PY" >/dev/null 2>&1; then
  "$PY" "$REPO/eng/packaging/verify_packages.py" "$OUT" --natives staged \
    || { echo "!!! PACKAGE VERIFICATION FAILED"; failed=$((failed + 1)); }
else
  echo "!!! python not found; skipping package verification"
  failed=$((failed + 1))
fi

echo
echo "=== packages in $OUT ==="
ls -la "$OUT"/*.nupkg "$OUT"/*.snupkg 2>/dev/null | awk '{printf "  %-46s %10s B\n", $9, $5}'

echo
echo "PACK_FAILED=$failed"
exit $failed
