#!/usr/bin/env bash
# Focused retest of the Linux TTS cell after adding --max-tokens to the probe.
#
# The full matrix pinned no generation budget, so qwen3_tts ran against the loader
# default of 2048 codec frames. On CUDA the talker samples an EOS after ~27 frames
# and the cell passes; on CPU it grinds and, with a 142 s clone reference, exceeds
# RAM. This script syncs the updated probe into the materialised build root, rebuilds
# only the e2e target, and re-runs the cell with a short reference + small budget.
#
# Usage: linux-tts-retest.sh <cpu|cuda> [max_tokens]
set -uo pipefail

SRC=/mnt/d/SouceCode/python2net/audio
REPO=$SRC/audiocpp-dotnet
A=$REPO/build/artifacts
M=${AUDIOCPP_MATRIX_ROOT:-$HOME/audio-matrix}
BACK=${1:-cpu}
MAXTOK=${2:-48}
REF=$A/ref-12s-16k.wav

export PATH=$(echo "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd: -)
export PATH=/usr/local/cuda/bin:$PATH
unset CUDA_PATH CUDA_HOME http_proxy https_proxy HTTP_PROXY HTTPS_PROXY

BD=$M/build-linux-$BACK
SRCDIR=$M/audiocpp-dotnet

echo "=== sync updated probe ==="
cp "$REPO/native/tests/e2e_probe.cpp" "$SRCDIR/native/tests/e2e_probe.cpp"
ls -la "$SRCDIR/native/tests/e2e_probe.cpp"

echo
echo "=== rebuild e2e target ($BACK) ==="
time cmake --build "$BD" --target audiocpp_dotnet_e2e --parallel 20 2>&1 | tail -15

E2E=$(find "$BD" -maxdepth 2 -name audiocpp_dotnet_e2e -type f | head -1)
echo "e2e: $E2E"
ls -la "$E2E"

OUT=/tmp/tts-$BACK-retest.wav
OUTLOG=/tmp/tts-$BACK-retest.out
export LD_LIBRARY_PATH="/usr/local/cuda/lib64:${LD_LIBRARY_PATH:-}"

echo
echo "=== tts ($BACK) ref=$REF max_tokens=$MAXTOK ==="
free -m | head -2
START=$(date +%s)
"$E2E" tts "$REPO/models/Qwen3-TTS-12Hz-0.6B-Base-GGUF" "Hello from the Linux full-set matrix." "$OUT" \
  --voice-ref "$REF" --ref-text "Jing Ling Ke Ji" --max-tokens "$MAXTOK" \
  --family qwen3_tts --backend "$BACK" > "$OUTLOG" 2>&1 &
PID=$!

PEAK=0
while kill -0 "$PID" 2>/dev/null; do
  if [ -r "/proc/$PID/status" ]; then
    RSS=$(awk '/^VmHWM:/{print $2}' "/proc/$PID/status" 2>/dev/null)
    if [ -n "${RSS:-}" ] && [ "$RSS" -gt "$PEAK" ]; then PEAK=$RSS; fi
  fi
  sleep 2
done
wait "$PID"
RC=$?
END=$(date +%s)

echo "--- child output ---"
tail -8 "$OUTLOG"
echo "--- result ---"
echo "exit=$RC  elapsed=$((END-START))s  peakRSS=$((PEAK/1024))MB ($(awk -v r="$PEAK" 'BEGIN{printf "%.2f", r/1048576}')GB)"
ls -la "$OUT" 2>/dev/null || echo "no output wav"
