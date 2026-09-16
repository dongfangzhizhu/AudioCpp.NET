#!/usr/bin/env bash
# Diagnose the Linux CPU TTS OOM.
#
# The matrix cell (Linux + cpu + qwen3_tts + voice-clone) was OOM-killed at
# ~28 GB anon-rss on a 31.8 GB host. This script re-runs that exact call with a
# parameterised shape so we can tell whether the residency comes from the thread
# pool, from the voice-clone (ICL) path, or from the model itself.
#
# /usr/bin/time is not installed in this Debian image, so peak RSS is sampled from
# /proc/<pid>/status (VmHWM) while the child runs.
#
# Usage: linux-cpu-tts-probe.sh [threads] [mode] [reference-wav]
#   threads : ggml thread count (default 4)
#   mode    : clone (default, passes --voice-ref/--ref-text) | plain (text only)
#   ref     : clone reference WAV (default the 142 s jinguling clip)
set -uo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
A=$REPO/build/artifacts
M=${AUDIOCPP_MATRIX_ROOT:-$HOME/audio-matrix}
THREADS=${1:-4}
MODE=${2:-clone}
WAV=${3:-$A/fixture-16k.wav}

export PATH=$(echo "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd: -)
unset CUDA_PATH CUDA_HOME http_proxy https_proxy HTTP_PROXY HTTPS_PROXY

BD=$M/build-linux-cpu
E2E=$(find "$BD" -maxdepth 2 -name audiocpp_dotnet_e2e -type f | head -1)
SHIM=$(find "$BD" -name 'libaudiocpp_dotnet_native.so' -not -path '*CMakeFiles*' | head -1)
MODEL=$REPO/models/Qwen3-TTS-12Hz-0.6B-Base-GGUF
OUT=/tmp/tts-cpu-${MODE}-t${THREADS}.wav
OUTLOG=/tmp/tts-cpu-${MODE}-t${THREADS}.out

REF_ARGS=()
if [ "$MODE" = clone ]; then
  REF_ARGS=(--voice-ref "$WAV" --ref-text "Jing Ling Ke Ji")
fi

echo "e2e    : $E2E"
echo "model  : $MODEL"
echo "threads: $THREADS   mode: $MODE"
echo "ref wav: $WAV"
echo "nproc  : $(nproc)"
echo "free before:"; free -m
echo

"$E2E" tts "$MODEL" "Hello from the Linux full-set matrix." "$OUT" \
  "${REF_ARGS[@]}" --family qwen3_tts --backend cpu --threads "$THREADS" > "$OUTLOG" 2>&1 &
PID=$!

PEAK=0
PEAK_VM=0
while kill -0 "$PID" 2>/dev/null; do
  if [ -r "/proc/$PID/status" ]; then
    RSS=$(awk '/^VmHWM:/{print $2}' "/proc/$PID/status" 2>/dev/null)
    VM=$(awk '/^VmPeak:/{print $2}' "/proc/$PID/status" 2>/dev/null)
    if [ -n "${RSS:-}" ] && [ "$RSS" -gt "$PEAK" ]; then PEAK=$RSS; fi
    if [ -n "${VM:-}" ] && [ "$VM" -gt "$PEAK_VM" ]; then PEAK_VM=$VM; fi
  fi
  sleep 2
done
wait "$PID"
RC=$?

echo "--- child output (tail) ---"
tail -12 "$OUTLOG"
echo "--- peak memory ---"
awk -v rss="$PEAK" -v vm="$PEAK_VM" -v th="$THREADS" -v md="$MODE" \
  'BEGIN{printf "mode             = %s\nthreads          = %d\npeak VmHWM (RSS) = %d kB (%.2f GB)\npeak VmPeak      = %d kB (%.2f GB)\n", md, th, rss, rss/1048576, vm, vm/1048576}'
echo
echo "probe exit=$RC"
ls -la "$OUT" 2>/dev/null || echo "no output wav"
echo "free after:"; free -m
