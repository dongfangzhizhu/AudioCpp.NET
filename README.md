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
```

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

## Interactive console

Run the console without arguments to open an interactive menu that walks
through package status, model download, ASR, TTS voice-clone, end-to-end
verification, and Hugging Face mirror settings step by step:

```powershell
dotnet run --project src/AudioCpp.NET.Console
```

Scripted subcommands are unchanged and `help` still prints the command
reference.

## Web workbench

A local Gradio-style test UI: browse and install model packages, pick a
detected local model, run ASR on an uploaded WAV, and synthesize voice-clone
TTS with inline playback and download. Managed requests are serialized
because the native runtime is not thread-safe; generated audio is served
only from the build artifacts directory and TLS verification stays on.

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
