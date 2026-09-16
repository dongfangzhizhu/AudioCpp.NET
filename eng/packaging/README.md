# Packaging and release

Three packages are published:

| Package | Contents | Built from |
| --- | --- | --- |
| `AudioCpp.NET` | `lib/net10.0/AudioCpp.NET.dll` + `AudioCpp.NET.Interop.dll` + XML docs | source |
| `AudioCpp.NET.Runtime` | `runtimes/{win-x64,linux-x64}/native/` — CPU shim | prebuilt native archives |
| `AudioCpp.NET.Runtime.Cuda` | same layout — CUDA shim | prebuilt native archives |

The two runtime packages deliberately share the native file name
(`audiocpp_dotnet_native.dll` / `libaudiocpp_dotnet_native.so`), so exactly one may be
referenced per project. Each ships a `buildTransitive` guard that turns that mistake
into a build error rather than letting the loaded backend depend on restore order.

## Scripts

| Script | Purpose |
| --- | --- |
| `stage-native.ps1` | Copy shims from the CMake trees (and from WSL) into `build/nuget-staging/<backend>/<rid>/` |
| `pack.ps1` | Stage + `dotnet pack` all three packages, then verify the layout |
| `make-native-archives.ps1` | Produce the `audiocpp-native-<rid>-<backend>.zip` release assets |
| `import-native-archives.sh` | Unpack those archives into the staging layout (used by CI) |
| `verify-packages.sh` | Assert the nupkgs contain the interop assembly and the native assets |
| `ShimStagingCheck.targets` | Fails `dotnet pack` when nothing is staged, instead of emitting an empty package |

## Release sequence

### 1. Build and verify all four platform cells

A released shim must be one that the matrix has actually exercised.

```powershell
powershell -File eng\matrix\win-full-matrix.ps1 -Configure
wsl -d Debian -- bash eng/matrix/linux-full-matrix.sh
```

Both scripts end with `FAILED=0` / `MATRIX_FAILED=0` when every cell passed, and copy
the shims they tested into `build/artifacts/matrix-out/<os>-<backend>/`. Staging reads
from there, so a published binary is by construction the tested one.

### 2. Produce the native archives

```powershell
powershell -File eng\packaging\make-native-archives.ps1
# build/native-archives/audiocpp-native-win-x64-cpu.zip
# build/native-archives/audiocpp-native-linux-x64-cpu.zip
# build/native-archives/audiocpp-native-win-x64-cuda.zip
# build/native-archives/audiocpp-native-linux-x64-cuda.zip
```

### 3. Attach them to a GitHub Release

Tag `v0.1.0` and attach the four archives to that release. The release workflow looks
them up by tag, so the version in the tag, the version in the archives and the version
of the packages must agree.

### 4. Run the release workflow

`Actions → release → Run workflow`, supplying the version and, if it differs, the
release tag. Leave **publish** unchecked first to inspect the packages as build
artifacts; re-run with it checked to push to nuget.org. Pushing a `v*.*.*` tag does the
same thing automatically.

The workflow needs the repository secret `NUGET_API_KEY` (a nuget.org key with
push permission for the three package IDs).

### 5. Local dry run (optional)

To build the packages without CI:

```powershell
powershell -File eng\packaging\pack.ps1 -Version 0.1.0
# build/nuget/*.nupkg
```

Publishing is then a manual `dotnet nuget push`. `pack.ps1` already runs
`verify-packages.sh`, so a package that packs cleanly has also had its layout checked.

## Notes

- **Why CI cannot build the natives.** A full-set shim compiles 74 model families plus
  the engine; the CUDA one needs the NVIDIA toolkit. Neither fits a GitHub-hosted
  runner. The archives are the seam: built on real hardware, consumed by CI.
- **Version.** `Directory.Build.props` holds `VersionPrefix`; release automation passes
  `-p:Version=<tag>` so the tag is authoritative.
- **Symbols.** The managed package ships a `.snupkg`. The asset-only packages disable
  symbol generation — there is no managed code in them, and the SDK rejects an empty
  symbols package with NU5017.
- **License.** Apache-2.0, inherited from the pinned engine. See `NOTICE`, which also
  records one unresolved question about a bundled upstream component.
