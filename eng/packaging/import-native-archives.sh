#!/usr/bin/env bash
# Unpack native shim archives into the staging layout the runtime packages expect.
#
# Archive name convention (produced by eng/packaging/make-native-archives.ps1):
#
#   audiocpp-native-<rid>-<backend>.zip     e.g. audiocpp-native-win-x64-cuda.zip
#
# Each archive is extracted to build/nuget-staging/<backend>/<rid>/, which is exactly
# where the .csproj files look for their runtimes/<rid>/native/ content:
#
#   build/nuget-staging/cpu/win-x64/audiocpp_dotnet_native.dll
#   build/nuget-staging/cpu/linux-x64/libaudiocpp_dotnet_native.so
#
# The archives are the only way native binaries enter a release build, which keeps
# the provenance of a published package traceable to a specific CI run or release
# asset rather than to whatever happened to be in someone's build directory.
#
# Usage:
#   eng/packaging/import-native-archives.sh <dir-with-zips> [staging-dir]
set -euo pipefail

SRC_DIR=${1:?usage: import-native-archives.sh <dir-with-zips> [staging-dir]}
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
STAGING=${2:-$REPO/build/nuget-staging}

shopt -s nullglob
zips=("$SRC_DIR"/audiocpp-native-*.zip)
shopt -u nullglob

if [ ${#zips[@]} -eq 0 ]; then
  echo "FATAL: no audiocpp-native-*.zip found in $SRC_DIR" >&2
  echo "       Archives are produced by eng/packaging/make-native-archives.ps1 and are" >&2
  echo "       expected as assets on the GitHub Release for this version." >&2
  exit 2
fi

for zip in "${zips[@]}"; do
  name=$(basename "$zip" .zip)                  # audiocpp-native-win-x64-cpu
  rest=${name#audiocpp-native-}                 # win-x64-cpu
  backend=${rest##*-}                           # cpu | cuda
  rid=${rest%-*}                                # win-x64 | linux-x64

  case "$backend" in
    cpu|cuda) ;;
    *) echo "FATAL: cannot parse backend from '$name'" >&2; exit 2 ;;
  esac

  dest="$STAGING/$backend/$rid"
  rm -rf "$dest"
  mkdir -p "$dest"
  unzip -q -o "$zip" -d "$dest"

  # Flatten in case the archive wrapped its payload in a directory.
  nested=$(find "$dest" -mindepth 2 -maxdepth 2 -type f \( -name 'audiocpp_dotnet_native.dll' -o -name 'libaudiocpp_dotnet_native.so' \) | head -1)
  if [ -n "$nested" ]; then
    mv "$nested" "$dest/"
    find "$dest" -mindepth 1 -maxdepth 1 -type d -empty -delete
  fi

  payload=$(find "$dest" -maxdepth 1 -type f \( -name 'audiocpp_dotnet_native.dll' -o -name 'libaudiocpp_dotnet_native.so' \))
  if [ -z "$payload" ]; then
    echo "FATAL: $zip did not contain a native shim library" >&2
    ls -la "$dest" >&2
    exit 2
  fi

  printf '  %-32s -> %-40s %s B\n' "$(basename "$zip")" "$dest" "$(stat -c%s $payload)"
done

echo
echo "staged runtimes:"
find "$STAGING" -type f \( -name '*.dll' -o -name '*.so' \) -printf '  %-60p %s B\n' | sort
