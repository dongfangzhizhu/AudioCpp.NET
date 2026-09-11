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

## Build native shim

Use the pinned checkout already present beside this repository:

```powershell
cmake -S . -B build/native -DAUDIOCPP_SRC=../audio.cpp -DAUDIOCPP_BACKEND=cpu
cmake --build build/native --config Release --target audiocpp_dotnet_native --parallel
```

The native build is intentionally separate from normal managed unit tests. End-to-end inference additionally requires a compatible model.

## Evaluate an upstream update

Fetch the candidate commit in the local `audio.cpp` checkout, then run:

```powershell
./eng/Compare-Upstream.ps1 -Candidate origin/main
```

The script compares the pinned commit with the candidate and classifies changes to runtime ABI dependencies, CMake/build files, model registry/specs, tests, and documentation. It never updates the lock file automatically.
