#!/usr/bin/env python3
"""Derive the 16 kHz mono PCM16 fixtures the matrix scripts feed to the e2e probe.

Every matrix cell needs a WAV that the pinned loaders accept:

* ``citrinet_asr`` and ``silero_vad`` both want 16 kHz mono input. Silero is strict
  about it (it rejects anything else outright), so the fixture has to be resampled
  rather than passed through.
* The qwen3_tts voice-clone reference drives ICL prompt residency, so the matrix
  hands it a *bounded* excerpt instead of the full clip: a 142 s reference peaked at
  ~28 GB and the WSL kernel OOM-killed the run, while 12 s peaks under 5 GB.

Both files are derived from a single source clip so the two sides of the matrix
cannot drift apart. The resampler is a plain linear interpolator: this is test
fixture data, not a reference resampler, and staying dependency-free means the
script behaves identically on Windows and inside WSL without ffmpeg or numpy.

Usage:
    python eng/matrix/tools/make-fixtures.py --src models/jinguling.wav \
        --outdir build/artifacts
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

TARGET_RATE = 16_000
DEFAULT_REF_SECONDS = 12


class WavError(RuntimeError):
    """Raised when the source file is not a WAV we can resample."""


def read_wav(path: Path) -> tuple[int, int, int, bytes]:
    """Return ``(sample_rate, channels, bits_per_sample, pcm_bytes)`` for a PCM WAV.

    Only uncompressed PCM (format tag 1) is understood; anything else is refused
    loudly rather than silently mis-read.
    """
    data = path.read_bytes()
    if len(data) < 12 or data[0:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise WavError(f"{path}: not a RIFF/WAVE file")

    pos = 12
    fmt: tuple[int, int, int, int] | None = None
    payload: bytes | None = None
    while pos + 8 <= len(data):
        chunk_id = data[pos : pos + 4]
        (chunk_size,) = struct.unpack_from("<I", data, pos + 4)
        body = data[pos + 8 : pos + 8 + chunk_size]
        if chunk_id == b"fmt ":
            if len(body) < 16:
                raise WavError(f"{path}: truncated fmt chunk")
            tag, channels, rate, _byte_rate, _align, bits = struct.unpack_from("<HHIIHH", body, 0)
            if tag != 1:
                raise WavError(f"{path}: expected PCM (format 1), found format {tag}")
            fmt = (channels, rate, bits, 0)
        elif chunk_id == b"data":
            payload = body
            break
        # Chunks are word-aligned: an odd size carries one pad byte.
        pos += 8 + chunk_size + (chunk_size & 1)

    if fmt is None:
        raise WavError(f"{path}: no fmt chunk")
    if payload is None:
        raise WavError(f"{path}: no data chunk")

    channels, rate, bits, _ = fmt
    if bits != 16:
        raise WavError(f"{path}: expected 16-bit samples, found {bits}")
    return rate, channels, bits, payload


def to_mono(pcm: bytes, channels: int) -> list[int]:
    """Downmix interleaved PCM16 to a list of mono samples."""
    samples = struct.unpack(f"<{len(pcm) // 2}h", pcm)
    if channels == 1:
        return list(samples)
    mono = []
    for index in range(0, len(samples) - channels + 1, channels):
        mono.append(sum(samples[index : index + channels]) // channels)
    return mono


def resample_linear(samples: list[int], src_rate: int, dst_rate: int) -> list[int]:
    """Linear-interpolation resampler. Adequate for fixtures, and dependency-free."""
    if src_rate == dst_rate or not samples:
        return samples
    ratio = dst_rate / src_rate
    out_len = int(len(samples) * ratio)
    out = [0] * out_len
    last = len(samples) - 1
    for i in range(out_len):
        src_pos = i / ratio
        left = int(src_pos)
        if left >= last:
            out[i] = samples[last]
            continue
        frac = src_pos - left
        out[i] = int(samples[left] + (samples[left + 1] - samples[left]) * frac)
    return out


def write_wav(path: Path, samples: list[int], rate: int) -> None:
    """Write mono PCM16 with a canonical 44-byte header."""
    pcm = struct.pack(f"<{len(samples)}h", *samples)
    header = (
        b"RIFF"
        + struct.pack("<I", 36 + len(pcm))
        + b"WAVE"
        + b"fmt "
        + struct.pack("<I", 16)
        + struct.pack("<HHIIHH", 1, 1, rate, rate * 2, 2, 16)
        + b"data"
        + struct.pack("<I", len(pcm))
    )
    path.write_bytes(header + pcm)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--src", required=True, type=Path, help="source WAV (any rate, mono or multi-channel PCM16)")
    parser.add_argument("--outdir", required=True, type=Path, help="directory to write fixtures into")
    parser.add_argument("--ref-seconds", type=int, default=DEFAULT_REF_SECONDS,
                        help=f"length of the bounded TTS clone reference (default {DEFAULT_REF_SECONDS})")
    args = parser.parse_args()

    if not args.src.is_file():
        print(f"FATAL: source clip not found: {args.src}", file=sys.stderr)
        return 2

    src_rate, channels, _bits, pcm = read_wav(args.src)
    mono = to_mono(pcm, channels)
    resampled = resample_linear(mono, src_rate, TARGET_RATE)

    args.outdir.mkdir(parents=True, exist_ok=True)
    full = args.outdir / "fixture-16k.wav"
    ref = args.outdir / f"ref-{args.ref_seconds}s-16k.wav"

    write_wav(full, resampled, TARGET_RATE)
    ref_samples = min(len(resampled), args.ref_seconds * TARGET_RATE)
    write_wav(ref, resampled[:ref_samples], TARGET_RATE)

    print(f"source            : {args.src} ({src_rate} Hz, {channels}ch, {len(mono) / src_rate:.2f}s)")
    print(f"fixture-16k.wav   : {len(resampled) / TARGET_RATE:.2f}s @ {TARGET_RATE} Hz -> {full}")
    print(f"ref-{args.ref_seconds}s-16k.wav : {ref_samples / TARGET_RATE:.2f}s @ {TARGET_RATE} Hz -> {ref}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
