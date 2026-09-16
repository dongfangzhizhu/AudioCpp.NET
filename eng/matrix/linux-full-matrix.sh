#!/usr/bin/env bash
# Linux full-set matrix: configure + build + smoke + real-inference e2e + managed
# tests for BOTH backends, using AUDIOCPP_MODEL_SET=full (all 71 model targets).
#
# Mirrors eng/matrix/win-full-matrix.ps1 so the two OS halves of the test matrix are
# produced by equivalent, reproducible scripts rather than ad-hoc commands.
#
# Runs entirely inside WSL Debian. Logs are written back into the repo's build/
# tree, which is gitignored and shared with the host over the /mnt/d mount.
#
# Notes learned the hard way:
#   * The login shell inherits the Windows PATH through interop, which contains
#     /mnt/c/.../CUDA/v13.3/bin/nvcc.exe. CMake's find_program(nvcc) would happily
#     pick that Windows binary and then fail every try_compile. Strip it first.
#   * FetchContent for boringssl has to hit github.com, which is unreliable here;
#     reuse the copy the Windows build already downloaded instead.
#   * TTS voice-clone must NOT be handed the full 142 s jinguling clip. qwen3_tts
#     runs the reference through the codec encoder and the ICL prompt, so residency
#     scales with reference length: 142 s peaked at ~28 GB and the kernel OOM-killer
#     took the process; a 12 s excerpt peaks under 5 GB. Generation is also pinned
#     with --max-tokens because the loader default (2048 codec frames) is what makes
#     a CPU-only run grind for hours when the talker does not sample EOS early.
#
# Layout:
#   $HOME/audio-matrix/build-linux-<backend>  CMake build trees (outside the 9p mount:
#                                             linking across it is painfully slow)
#   build/artifacts/                          fixtures, per-cell logs and shims
#   build/deps/boringssl-src                  offline FetchContent source cache
#
# Overridable via env: AUDIOCPP_MATRIX_ROOT, AUDIOCPP_UPSTREAM, AUDIOCPP_FIXTURE_SRC
set -uo pipefail

SRC=/mnt/d/SouceCode/python2net/audio
REPO=$SRC/audiocpp-dotnet
A=$REPO/build/artifacts
M=${AUDIOCPP_MATRIX_ROOT:-$HOME/audio-matrix}
PIN=78d47706c30ef215ba9ad3559baff309efeb5260
UPSTREAM=${AUDIOCPP_UPSTREAM:-$HOME/audiocpp-build/audio-pinned}
WAV=$A/fixture-16k.wav
TTS_REF=$A/ref-12s-16k.wav
TTS_MAX_TOKENS=48
FAILED=0
JOBS=$(nproc)
[ "$JOBS" -gt 20 ] && JOBS=20

step_failed() { echo "!!! STEP FAILED: $1"; FAILED=1; }
step() { echo; echo "=============== [$(date +%T)] $* ==============="; }

# ── environment hygiene ───────────────────────────────────────────────────────
# Drop interop paths (they shadow the Linux CUDA toolkit) and any duplicate
# proxy variables, which crash build tooling with case-insensitive collisions.
export PATH=$(echo "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd: -)
export PATH=/usr/local/cuda/bin:$PATH
unset CUDA_PATH CUDA_HOME http_proxy https_proxy HTTP_PROXY HTTPS_PROXY

step "toolchain"
cmake --version | head -1
g++ --version | head -1
/usr/local/cuda/bin/nvcc --version 2>&1 | tail -1
ninja --version
export PATH="/opt/dotnet:$HOME/.dotnet:$PATH"
dotnet --version 2>/dev/null || echo "no dotnet"
nproc; free -g | head -2

step "fixtures"
mkdir -p "$A"
if [ ! -f "$WAV" ] || [ ! -f "$TTS_REF" ]; then
  FIXTURE_SRC=${AUDIOCPP_FIXTURE_SRC:-$REPO/models/jinguling.wav}
  [ -f "$FIXTURE_SRC" ] || { echo "FATAL: no fixture source clip at $FIXTURE_SRC"; exit 2; }
  python3 "$REPO/eng/matrix/tools/make-fixtures.py" --src "$FIXTURE_SRC" --outdir "$A" \
    || { echo "FATAL: fixture generation failed"; exit 2; }
fi
echo "asr/vad fixture: $WAV ($(ls -l "$WAV" | awk '{print $5}') bytes)"
echo "tts clone ref  : $TTS_REF ($(ls -l "$TTS_REF" | awk '{print $5}') bytes)"

step "preflight"
test -d "$UPSTREAM/.git" || { echo "FATAL: missing upstream checkout $UPSTREAM"; exit 2; }
ACTUAL=$(git -C "$UPSTREAM" rev-parse HEAD 2>/dev/null)
[ "$ACTUAL" = "$PIN" ] || { echo "FATAL: upstream HEAD=$ACTUAL, expected $PIN"; exit 2; }
echo "upstream pin OK: $ACTUAL"

# Silero VAD assets ship inside the upstream checkout; find whichever root has them.
SILERO=""
for candidate in "$SRC/audio-pinned/assets/framework/models/silero_vad" "$SRC/audio.cpp/assets/framework/models/silero_vad" "$UPSTREAM/assets/framework/models/silero_vad"; do
  if [ -d "$candidate" ]; then SILERO=$candidate; break; fi
done
[ -n "$SILERO" ] || { echo "FATAL: silero_vad assets not found"; exit 2; }
echo "silero vad: $SILERO"

step "materialise build root: $M"
mkdir -p "$M/deps"
rm -rf "$M/audiocpp-dotnet"; mkdir -p "$M/audiocpp-dotnet"
tar -C "$REPO" --exclude='.git' --exclude='build' --exclude='models' -cf - . | tar -xf - -C "$M/audiocpp-dotnet"
echo "project files: $(find "$M/audiocpp-dotnet" -type f | wc -l)"

# boringssl source: copy off the 9p mount once so FetchContent does not download.
if [ ! -f "$M/deps/boringssl-src/CMakeLists.txt" ]; then
  rm -rf "$M/deps/boringssl-src"
  cp -r "$REPO/build/deps/boringssl-src" "$M/deps/boringssl-src" 2>/dev/null
fi
test -f "$M/deps/boringssl-src/CMakeLists.txt" || { echo "FATAL: boringssl source missing"; exit 2; }
echo "boringssl cached: $(ls "$M/deps/boringssl-src" | wc -l) entries"

cd "$M/audiocpp-dotnet"

for BACK in cpu cuda; do
  OUT="$A/matrix-out/linux-$BACK"; mkdir -p "$OUT"
  BD="$M/build-linux-$BACK"
  LOG="$A/build-linux-full-$BACK.log"

  EXTRA=(-DFETCHCONTENT_SOURCE_DIR_AUDIOCPP_BORINGSSL="$M/deps/boringssl-src")
  if [ "$BACK" = cuda ]; then
    EXTRA+=(-DCUDAToolkit_ROOT=/usr/local/cuda -DCMAKE_CUDA_ARCHITECTURES=89 -DCMAKE_CUDA_COMPILER=/usr/local/cuda/bin/nvcc)
  fi

  step "configure $BACK (full set)"
  rm -rf "$BD"
  cmake -S . -B "$BD" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DAUDIOCPP_SRC="$UPSTREAM" \
    -DAUDIOCPP_BACKEND=$BACK \
    -DAUDIOCPP_MODEL_SET=full \
    -DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON \
    "${EXTRA[@]}" > "$LOG" 2>&1 \
    || { step_failed "configure-$BACK"; tail -25 "$LOG"; continue; }

  # Guard against the interop PATH leaking a Windows nvcc into the cache.
  if grep -q "/mnt/c" "$BD/CMakeCache.txt"; then
    echo "WARNING: build cache references /mnt/c paths"
    grep -n "/mnt/c" "$BD/CMakeCache.txt" | head -5
    step_failed "interop-path-leak-$BACK"
  fi
  grep -a "model composite" "$LOG" | head -1
  echo "loaders: $(grep -c 'make_' "$BD/audio_cpp/generated/model_registry_loaders.inc")"

  step "build $BACK (-j$JOBS)"
  cmake --build "$BD" --target audiocpp_dotnet_native audiocpp_dotnet_abi_smoke audiocpp_dotnet_e2e --parallel "$JOBS" \
    >> "$LOG" 2>&1 || { step_failed "build-$BACK"; tail -30 "$LOG"; continue; }
  echo "BUILD OK"

  SHIM=$(find "$BD" -name 'libaudiocpp_dotnet_native.so' -not -path '*CMakeFiles*' | head -1)
  SMOKE=$(find "$BD" -maxdepth 2 -name 'audiocpp_dotnet_abi_smoke' -type f | head -1)
  E2E=$(find "$BD" -maxdepth 2 -name 'audiocpp_dotnet_e2e' -type f | head -1)
  echo "shim: $SHIM"
  ls -lh "$SHIM" 2>&1
  cp "$SHIM" "$OUT/" 2>/dev/null
  cp "$SMOKE" "$OUT/" 2>/dev/null
  cp "$E2E" "$OUT/" 2>/dev/null

  export LD_LIBRARY_PATH="/usr/local/cuda/lib64:${LD_LIBRARY_PATH:-}"

  step "abi smoke ($BACK)"
  "$SMOKE" > "$OUT/abi-smoke.log" 2>&1; RC=$?
  cat "$OUT/abi-smoke.log"
  echo "abi_smoke exit=$RC"; [ $RC -eq 0 ] || step_failed "abi-smoke-$BACK"

  step "e2e asr ($BACK)"
  "$E2E" asr "$REPO/models/Citrinet-ASR-GGUF" "$WAV" --family citrinet_asr --backend $BACK > "$OUT/e2e-asr.log" 2>&1
  RC=$?; tail -3 "$OUT/e2e-asr.log"; echo "e2e asr exit=$RC"; [ $RC -eq 0 ] || step_failed "e2e-asr-$BACK"

  step "e2e vad ($BACK)"
  "$E2E" vad "$SILERO" "$WAV" --family silero_vad --backend $BACK > "$OUT/e2e-vad.log" 2>&1
  RC=$?; tail -3 "$OUT/e2e-vad.log"; echo "e2e vad exit=$RC"; [ $RC -eq 0 ] || step_failed "e2e-vad-$BACK"

  step "e2e tts ($BACK)"
  "$E2E" tts "$REPO/models/Qwen3-TTS-12Hz-0.6B-Base-GGUF" "Hello from the Linux full-set matrix." "$OUT/tts.wav" \
    --voice-ref "$TTS_REF" --ref-text "Jing Ling Ke Ji" --max-tokens "$TTS_MAX_TOKENS" \
    --family qwen3_tts --backend $BACK > "$OUT/e2e-tts.log" 2>&1
  RC=$?; tail -3 "$OUT/e2e-tts.log"; echo "e2e tts exit=$RC"; [ $RC -eq 0 ] || step_failed "e2e-tts-$BACK"
  test -s "$OUT/tts.wav" || step_failed "tts-wav-$BACK"

  # Managed suite runs against EVERY backend shim, mirroring the Windows matrix, so
  # the interop + managed layers are proven on both backends on both OSes.
  step "managed tests against the full $BACK shim"
  AUDIOCPP_TEST_NATIVE="$SHIM" dotnet test tests/AudioCpp.NET.Tests --nologo -v q \
    > "$OUT/managed-tests.log" 2>&1 || step_failed "managed-tests-$BACK"
  tail -4 "$OUT/managed-tests.log"
done

step "ALL DONE (FAILED=$FAILED)"
exit $FAILED
