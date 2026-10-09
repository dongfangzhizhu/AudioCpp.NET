# AudioCpp.NET native runtime

Native `audio.cpp` shim bundled as `runtimes/<rid>/native/` assets for
[AudioCpp.NET](https://www.nuget.org/packages/AudioCpp.NET). This package contains
no managed code; reference it alongside the managed package to supply the engine.

| Package | Backend | RIDs |
| --- | --- | --- |
| `AudioCpp.NET.Runtime` | CPU (portable, no GPU) | `win-x64`, `linux-x64` |
| `AudioCpp.NET.Runtime.Cuda` | NVIDIA GPU via CUDA | `win-x64`, `linux-x64` |
| `AudioCpp.NET.Runtime.Vulkan` | Any Vulkan 1.2+ GPU (NVIDIA / AMD / Intel) | `win-x64`, `linux-x64` |

## Usage

```xml
<PackageReference Include="AudioCpp.NET" Version="0.3.0" />
<PackageReference Include="AudioCpp.NET.Runtime" Version="0.3.0" />
```

Reference **exactly one** runtime package. All three provide the same native file name
(`audiocpp_dotnet_native.dll` / `libaudiocpp_dotnet_native.so`), so referencing two
would make the loaded backend depend on restore ordering. The packages detect that
case and fail the build with an explanatory error instead.

The SDK copies the asset matching your RID next to the application. To override the
location, set `AudioCppRuntimeOptions.NativeLibraryPath` or `AUDIOCPP_NATIVE_PATH`.

## Backends

The backend is compiled into the shim, not selected at run time. Query it with
`AudioCppRuntime.Backend`; all three builds expose the same managed API and the same
model catalog, so switching packages does not change your code.

The CUDA shim statically links its kernels, which is why it is substantially larger
than the CPU one. The Vulkan shim compiles its shaders at build time and loads the
Vulkan driver at run time, so it ships no GPU runtime of its own — like the CPU
package, it redistributes only the shim.

## Requirements

- Windows: x64, MSVC 2022+ runtime; for CUDA a driver new enough for CUDA 13; for
  Vulkan a driver exposing a Vulkan 1.2+ ICD (any current GPU driver does)
- Linux: x64, glibc 2.35+ (Debian 12 / Ubuntu 22.04 or newer); the CUDA build also
  needs `libcudart` available at run time, the Vulkan build `libvulkan.so.1`
  (package `libvulkan1`, a dependency of every GPU driver package)

## License

Apache-2.0. The shim links the pinned
[audio.cpp](https://github.com/0xShug0/audio.cpp) engine, also Apache-2.0
(Copyright 2026 ShugoAI LLC). See the repository `NOTICE` and `LICENSE`.
