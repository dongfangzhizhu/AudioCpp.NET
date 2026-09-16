# AudioCpp.NET

.NET 10 bindings for [audio.cpp](https://github.com/0xShug0/audio.cpp): speech
recognition, text-to-speech with voice cloning, voice activity detection and the
engine's other tasks, reached through a small versioned C ABI shim.

This package contains the managed API. It needs a native shim at run time, which
ships in a separate runtime package — reference **exactly one** of:

| Package | Backend | RIDs |
| --- | --- | --- |
| `AudioCpp.NET.Runtime` | CPU | `win-x64`, `linux-x64` |
| `AudioCpp.NET.Runtime.Cuda` | NVIDIA GPU (CUDA) | `win-x64`, `linux-x64` |

```xml
<PackageReference Include="AudioCpp.NET" Version="0.1.0" />
<PackageReference Include="AudioCpp.NET.Runtime" Version="0.1.0" />
```

The two runtime packages carry the same native file name, so referencing both is a
build error rather than a silent coin-flip over which backend wins.

## Quick start

```csharp
using AudioCpp.NET;

using var runtime = AudioCppRuntime.Create(new AudioCppRuntimeOptions
{
    NativeLibraryPath = null,      // resolved from the runtime package
    ModelsDirectory = "models",
});

var models = runtime.ListPackages();          // 217 downloadable packages
Console.WriteLine(runtime.Backend);           // "cpu" or "cuda"
```

Loading a model validates the directory against its manifest first, so a partial
download reports the missing files instead of failing inside native code:

```csharp
using var model = runtime.LoadModel("models/Qwen3-TTS-12Hz-0.6B-Base-GGUF", "qwen3_tts");
var result = model.Run(new AudioCppRunRequest { Text = "Hello from AudioCpp.NET." });
foreach (var clip in result.AudioClips) clip.Save("out.wav");
```

## Backends and native library resolution

The shim is looked up in this order, stopping at the first hit:

1. `AudioCppRuntimeOptions.NativeLibraryPath`
2. the `AUDIOCPP_NATIVE_PATH` environment variable
3. the bare library name next to the application
4. a repository-layout probe (for running from source: `build/native*/` and
   `runtimes/<rid>/native/`)

`AudioCppRuntime.Create` reports the compiled-in backend, so a CPU package on a
GPU-less machine is detectable rather than a crash.

## Models

Model weights are not redistributed here — they are multi-GB and licensed
separately. Install them with the companion CLI:

```
dotnet run --project src/AudioCpp.NET.Console -- models list
dotnet run --project src/AudioCpp.NET.Console -- models download <id> --models-dir models
```

## Requirements

- .NET 10
- A native runtime package for your RID
- Windows: the MSVC 2022+ x64 runtime; Linux: glibc 2.35+ (Debian 12 / Ubuntu 22.04
  or newer)
- CUDA package: an NVIDIA driver new enough for CUDA 13

## License

Apache-2.0. The native shims link the pinned audio.cpp engine, also Apache-2.0
(Copyright 2026 ShugoAI LLC); see the `NOTICE` file in the repository.
