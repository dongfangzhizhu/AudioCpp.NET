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
# asset rather than to whatever happened to be in someone's build directory. Their
# own provenance — which audio.cpp commit and shim ABI they were built from — travels
# in audiocpp-native-manifest.json next to them and is checked against the pin below.
#
# Usage:
#   eng/packaging/import-native-archives.sh <dir-with-zips> [staging-dir]
#
# Provenance check:
#   audiocpp-native-manifest.json, produced next to the archives by
#   eng/packaging/make-native-archives.ps1, records the audio.cpp commit and shim
#   ABI the binaries were built from. When it disagrees with eng/upstream.lock.json
#   the archives belong to a different pin than the managed package that will ship
#   beside them, and consumers would hit missing exports at runtime rather than at
#   install time. Set AUDIOCPP_ENFORCE_NATIVE_PIN=1 (what the tag-triggered release
#   does) to fail on that; the default only warns, which keeps hand-staged archives
#   usable for a local pack. A missing manifest is treated as "unknown", not as a
#   mismatch: archives from before this file existed stay usable with a warning.
set -euo pipefail

SRC_DIR=${1:?usage: import-native-archives.sh <dir-with-zips> [staging-dir]}
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
STAGING=${2:-$REPO/build/nuget-staging}
ENFORCE=${AUDIOCPP_ENFORCE_NATIVE_PIN:-0}

shopt -s nullglob
zips=("$SRC_DIR"/audiocpp-native-*.zip)
shopt -u nullglob

if [ ${#zips[@]} -eq 0 ]; then
  echo "FATAL: no audiocpp-native-*.zip found in $SRC_DIR" >&2
  echo "       Archives are produced by eng/packaging/make-native-archives.ps1 and are" >&2
  echo "       expected as assets on the GitHub Release for this version." >&2
  exit 2
fi

# --- provenance ---------------------------------------------------------------
# Archives are not rebuildable in CI, so they can be carried over from an older
# release by mistake. Compare the manifest against the pinned commit and refuse to
# stage a runtime package whose ABI does not match the managed package being shipped.
check_native_pin() {
  local manifest="$SRC_DIR/audiocpp-native-manifest.json"
  local lock="$REPO/eng/upstream.lock.json"

  if [ ! -f "$manifest" ]; then
    if [ "$ENFORCE" = "1" ]; then
      echo "ERROR no audiocpp-native-manifest.json next to the archives, so their audio.cpp" >&2
      echo "      pin cannot be verified against $lock." >&2
      echo "      Rebuild them with eng/packaging/make-native-archives.ps1, which writes the" >&2
      echo "      manifest next to the archives, and attach it to the release as well." >&2
      return 1
    fi
    echo "WARN  no audiocpp-native-manifest.json next to the archives; their audio.cpp pin is unknown."
    echo "      Rebuild them with eng/packaging/make-native-archives.ps1 so the release can verify the pin."
    return 0
  fi

  local want got abi_major abi_minor
  want=$(sed -n 's/.*"commit": *"\([0-9a-f]\{40\}\)".*/\1/p' "$lock" | head -1)
  got=$(sed -n 's/.*"audioCppCommit": *"\([0-9a-f]\{40\}\)".*/\1/p' "$manifest" | head -1)
  if [ -z "$want" ] || [ -z "$got" ]; then
    echo "WARN  could not read the commit from $lock or $manifest; skipping the pin check."
    return 0
  fi
  abi_major=$(sed -n 's/.*"shimAbiMajor": *\([0-9][0-9]*\).*/\1/p' "$manifest" | head -1)
  abi_minor=$(sed -n 's/.*"shimAbiMinor": *\([0-9][0-9]*\).*/\1/p' "$manifest" | head -1)

  if [ "$want" != "$got" ]; then
    echo "ERROR native archives were built for a different audio.cpp pin:" >&2
    echo "        archives : $got" >&2
    echo "        pinned   : $want" >&2
    echo "        Publishing them would pair a managed package built against shim ABI ${abi_major}.${abi_minor}" >&2
    echo "        with a runtime package that does not implement it. Rebuild the archives for this pin" >&2
    echo "        (eng/packaging/make-native-archives.ps1) and attach them to the release again." >&2
    if [ "$ENFORCE" = "1" ]; then
      return 1
    fi
    echo "WARN  continuing anyway (AUDIOCPP_ENFORCE_NATIVE_PIN=$ENFORCE); the runtime packages are NOT safe to publish." >&2
    return 0
  fi

  echo "native archives match the pin: $got (shim ABI ${abi_major}.${abi_minor})"
}

if ! check_native_pin; then
  exit 3
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
