# AudioCpp.NET

.NET 10 bindings for [audio.cpp](https://github.com/0xShug0/audio.cpp), using a small versioned C ABI shim over `engine_runtime`.

> Development status: initial ABI/TTS foundation. The upstream source is pinned; this repository never builds an unreviewed moving `main` branch.

## Pinned upstream

- Repository: `https://github.com/0xShug0/audio.cpp`
- Commit: `78d47706c30ef215ba9ad3559baff309efeb5260`
- Lock file: [`eng/upstream.lock.json`](eng/upstream.lock.json)

Read [`PLAN.md`](PLAN.md) for architecture, scope, milestones, upgrade policy, risks, and acceptance criteria.

## Build managed projects

```powershell
dotnet build AudioCpp.NET.slnx
dotnet test AudioCpp.NET.slnx
```

## Console model tools

The console project lists the model loaders compiled into the pinned native
shim and can install a package through audio.cpp's native model manager:

```powershell
dotnet run --project src/AudioCpp.NET.Console -- models list --native .\build\native-default\Release\audiocpp_dotnet_native.dll
dotnet run --project src/AudioCpp.NET.Console -- models path
dotnet run --project src/AudioCpp.NET.Console -- models download qwen3_tts_1_7b_base_q8_0 --models-dir .\models
# Use a Hugging Face mirror for faster downloads
dotnet run --project src/AudioCpp.NET.Console -- models download citrinet_asr_q8_0 `
  --models-dir .\models --hf-endpoint https://hf-mirror.com
# Re-check every installed package against its manifest at any time
dotnet run --project src/AudioCpp.NET.Console -- models verify --models-dir .\models
```

Every `models download` automatically verifies the freshly installed package
against its `.audiocpp-package-<id>.json` manifest (the package manager records
each expected file with its byte size and SHA-256 etag). Missing files or size
mismatches are reported with the exact paths, and the command exits non-zero so
scripts can react. `models verify` re-checks all installed packages on demand.
Loading a model directory runs the same check **before** the native call, so an
interrupted or partial download fails fast with `Model at '…' is incomplete.
missing: … (expected N byte(s)) Re-download the package or restore the missing
files.` instead of a cryptic native "missing file" error.

Hugging Face downloads support `--hf-endpoint URL` on `models download` and
`verify`. The option takes precedence over `AUDIOCPP_HF_BASE_URL` and
`HF_ENDPOINT`. For scripts, set `HF_ENDPOINT` (the native downloader also
accepts this standard Hugging Face variable); `AUDIOCPP_HF_BASE_URL` remains
the native-specific override and takes precedence over `HF_ENDPOINT` when the
CLI option is not supplied. Endpoints must be absolute HTTP(S) URLs without
credentials, query strings, or fragments. TLS certificate and host-name
verification remain enabled.

The default native build supports `models list`. Build with
`-DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON` to enable `models packages` and
`models download`; that option requires the upstream package manager's TLS
dependency (system OpenSSL or the pinned BoringSSL archive).

## Streaming

The shim exposes upstream's `IStreamingVoiceTaskSession` through four additive
ABI exports (`audiocpp_stream_open`, `audiocpp_stream_push_pcm`,
`audiocpp_stream_finish`, `audiocpp_stream_free`; `stream_open_ex` additionally
accepts inline text, style and input artifacts), advertises
`AUDIOCPP_CAP_STREAMING`, and reports ABI minor 3. `stream_open` rejects models
whose loader does not list a `streaming` mode for the requested task, so offline
models fail fast with `model does not support streaming for the requested task`.

Managed code opens a session with `AudioCppModel.StartStreaming(task, options)`,
feeds it with `PushPcm(samples, sampleRate, channels)` (each call returns the
typed events produced by that chunk: partial text, voice-activity start/end
markers, audio output), and completes it with `Finish()`, which yields the same
`AudioCppTaskResult` shape as the structured offline path.

In this pinned build only `silero_vad` ships a streaming mode; its bundled
weights live in the upstream checkout at
`assets/framework/models/silero_vad/silero_vad_16k.safetensors`. The console
`vad` command streams a file through it:

```powershell
dotnet run --project src/AudioCpp.NET.Console -- vad `
  --input ..\audio-pinned\assets\resources\sample_16k.wav `
  --model ..\audio-pinned\assets\framework\models\silero_vad\silero_vad_16k.safetensors `
  --family silero_vad
```

Silero requires 16 kHz input and exact 512-sample chunks; the CLI follows the
session's advertised policy and zero-pads the final chunk. `asr --stream` runs
the same loop for any future streaming ASR loader.

## Interactive console

Run the console without arguments to open an interactive menu that walks
through package status, model download, ASR, TTS voice-clone, end-to-end
verification, and Hugging Face mirror settings step by step:

```powershell
dotnet run --project src/AudioCpp.NET.Console
```

Scripted subcommands are unchanged and `help` still prints the command
reference. Two additional subcommands exercise the full ABI surface:

- `tasks --native PATH` prints the native task catalog: every canonical token
  (`vad/asr/diar/sep/gen/tts/clon/vc/s2s/align/vdes/spk/svc/midi`) plus the
  model-spec aliases (`audio_generation/music/sfx/edit/clone/design/speaker/codec`)
  with their input shape, typical outputs and aliases.
- `run --model DIR --task TOKEN [--input WAV | --text TEXT] [--text-language LANG]
  [--artifact kind:hex|kind:path ...] [--style-language LANG] [--emotion E]
  [--speaking-rate R] [--pitch-shift S] [--energy-scale X] [--style-tag k=v]
  [--option k=v ...]` runs any task through the structured-result ABI and prints
  the full JSON result (segments, turns, word timestamps, artifacts and audio
  clips written next to the output directory). Unknown tokens are rejected with
  the accepted-token list.

## Web workbench

A local Gradio-style test UI: browse and install model packages, pick a
detected local model, run ASR on an uploaded WAV, and synthesize voice-clone
TTS with inline playback and download. Managed requests are serialized
because the native runtime is not thread-safe; generated audio is served
only from the build artifacts directory and TLS verification stays on.

Every detected model shows a completeness badge (`✓ complete` / `✗ incomplete`
/ `unmanaged`) plus a **VERIFY** button that re-checks the package manifest on
demand (`POST /api/verify`). A failed install reports the missing files
directly in the studio log.

The **TASK CONSOLE** panel lists the native build's ABI info, capability flags
and the full task catalog (`GET /api/build`, `GET /api/tasks`), and drives the
generic run endpoint (`POST /api/run`): pick any task token, supply text
and/or a WAV, optional style/artifacts/options, and get the structured result —
segments, speaker turns, word timestamps, artifacts — with generated audio
clips rendered inline for playback and download.

The **VAD STREAM** deck streams an uploaded WAV through a streaming session
(`POST /api/stream`). It reports the negotiated policy, the chunk size actually
used, how many samples were zero-padded to keep a fixed-size window aligned, and
one line per event tagged with the offset of the chunk that produced it, followed
by the final speech segments. **PROBE POLICY**
(`GET /api/stream/policy?modelPath=…`) opens a session without pushing audio and
returns the same `input`/`output` policy and preferred chunk size, which is the
cheapest way to check whether a directory can stream at all. Streaming needs a
loader that advertises a `streaming` mode for the requested task (in this pinned
build only `silero_vad`), and that loader accepts 16 kHz input only.

The workbench serves `wwwroot` from the output directory, so run the built
executable from its own folder (or use `dotnet run`, which sets the content root
to the project).

```powershell
# Optional pre-configuration; every field stays editable in the UI
$env:AUDIOCPP_NATIVE_PATH = ".\build\native-verify\Release\audiocpp_dotnet_native.dll"
$env:AUDIOCPP_MODELS_DIR  = ".\models"
$env:HF_ENDPOINT          = "https://hf-mirror.com"
dotnet run --project src/AudioCpp.NET.Web --urls http://127.0.0.1:5099
```

## Build native shim

Use the pinned checkout already present beside this repository:

```powershell
cmake -S . -B build/native -DAUDIOCPP_SRC=../audio.cpp -DAUDIOCPP_BACKEND=cpu
cmake --build build/native --config Release --target audiocpp_dotnet_native --parallel
```

The native build is intentionally separate from normal managed unit tests. End-to-end inference additionally requires a compatible model.

### GPU (CUDA) builds

Set `-DAUDIOCPP_BACKEND=cuda` (plus `-DCUDAToolkit_ROOT=… -DCMAKE_CUDA_ARCHITECTURES=89`
for an RTX 4090) in the same commands. Read-only environment preflight checks:

```powershell
# Windows: driver, nvcc, MSVC, CUDA headers/VS integration
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Check-WindowsGpuBuild.ps1
# Debian/WSL: driver, nvcc, CUDA headers and libcudart.so
wsl -d Debian -- bash eng/check-debian-gpu-build.sh
```

On MSVC, the global `/utf-8` flag is scoped to C/C++ sources only (`COMPILE_LANG_AND_ID`
generator expressions) because nvcc parses a bare `/utf-8` as an extra input file and
aborts CUDA compilation.

## Troubleshooting

### `DllNotFoundException: Unable to load audiocpp_dotnet_native`

The native shim is a CMake artifact; `dotnet build` never copies it. The
loader resolves it in this order and stops at the first hit:

1. Explicit path: the Web UI *Native shim* field, the CLI `--native` option,
   or `AudioCppRuntimeOptions.NativeLibraryPath`
2. The `AUDIOCPP_NATIVE_PATH` environment variable
3. The bare library name in the application output directory or `PATH`
4. Repository auto-discovery: walking up from the application directory it
   probes `<root>/build/native*/Release|Debug` and `runtimes/<rid>/native`

When nothing matches, the exception lists every probed path. Build the shim
with the command above or point `AUDIOCPP_NATIVE_PATH` at an existing build.

## Evaluate an upstream update

Fetch the candidate commit in the local `audio.cpp` checkout, then run:

```powershell
./eng/Compare-Upstream.ps1 -Candidate origin/main
```

The script compares the pinned commit with the candidate and classifies changes to runtime ABI dependencies, CMake/build files, model registry/specs, tests, and documentation. It never updates the lock file automatically.
