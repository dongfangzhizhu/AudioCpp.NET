#!/usr/bin/env bash
# One-shot live memory snapshot of the running e2e TTS probe.
set -uo pipefail
free -m
echo "--- e2e processes ---"
for p in $(pgrep -f audiocpp_dotnet_e2e); do
  echo "pid=$p"
  awk '/^VmHWM:|^VmRSS:|^VmPeak:/{print "  " $0}' "/proc/$p/status" 2>/dev/null
done
echo "--- elapsed ---"
ps -o pid=,etime=,rss=,cmd= -C audiocpp_dotnet_e2e 2>/dev/null | head -3
