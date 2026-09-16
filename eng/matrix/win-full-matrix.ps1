# Windows full-set matrix: configure (optional) + build + smoke + real-inference e2e
# + managed tests, for BOTH backends, with AUDIOCPP_MODEL_SET=full.
#
# Mirrors eng/matrix/linux-full-matrix.sh so the two OS halves of the test matrix
# are produced by equivalent, reproducible scripts rather than ad-hoc commands.
#
# Everything runs inside the VS Developer environment (no cmd.exe): the security
# policy here blocks cmd.exe from both the Bash and PowerShell channels, and
# Enter-VsDevShell gives MSVC its INCLUDE/LIB without one.
#
# Memory note: this host has 32 GB, and the CUDA full build dies silently when nvcc
# runs -j24. CUDA therefore defaults to a small job count.
#
# Layout:
#   build\native-<backend>\   CMake build trees (gitignored; build/native-* is the
#                             pattern NativeLibraryLoader auto-discovers)
#   build\deps\boringssl-src  offline FetchContent source cache
#   build\artifacts\          fixtures, per-cell logs and the shims under test
#
# Usage:
#   powershell -File eng\matrix\win-full-matrix.ps1                 # reuse existing trees
#   powershell -File eng\matrix\win-full-matrix.ps1 -Configure      # reconfigure first
#   powershell -File eng\matrix\win-full-matrix.ps1 -Backends cpu
param(
    [string[]]$Backends = @("cpu", "cuda"),
    [switch]$Configure,
    [int]$CpuJobs = 24,
    [int]$CudaJobs = 6,
    [string]$FixtureSource = ""
)

# Native tools write progress to stderr; "Stop" would abort on the first warning.
$ErrorActionPreference = "Continue"

$vs = "C:\Program Files\Microsoft Visual Studio\18\Professional"
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null

$repo   = "D:\SouceCode\python2net\audio\audiocpp-dotnet"
$src    = "D:\SouceCode\python2net\audio\audio-pinned"
$A      = "$repo\build\artifacts"
$boring = "$repo\build\deps\boringssl-src"

$wav     = "$A\fixture-16k.wav"          # 16 kHz mono, for ASR / VAD
$ttsRef  = "$A\ref-12s-16k.wav"          # bounded clone reference for TTS
$maxTok  = 48

$asrModel = "$repo\models\Citrinet-ASR-GGUF"
$ttsModel = "$repo\models\Qwen3-TTS-12Hz-0.6B-Base-GGUF"
$silero   = "$src\assets\framework\models\silero_vad"

Remove-Item Env:http_proxy  -ErrorAction SilentlyContinue
Remove-Item Env:https_proxy -ErrorAction SilentlyContinue

Set-Location $repo
New-Item -ItemType Directory -Force -Path $A | Out-Null
$failed = 0

function Write-Step([string]$text) {
    Write-Output ""
    Write-Output ("=============== [" + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "] $text ===============")
}
function Mark-Failed([string]$name) {
    Write-Output "!!! STEP FAILED: $name"
    $script:failed++
}

# --- fixtures -----------------------------------------------------------------
Write-Step "fixtures"
if ([string]::IsNullOrWhiteSpace($FixtureSource)) {
    foreach ($candidate in @("$repo\models\jinguling.wav", "$repo\models\jinguling-16k.wav")) {
        if (Test-Path $candidate) { $FixtureSource = $candidate; break }
    }
}
if (-not (Test-Path $wav) -or -not (Test-Path $ttsRef)) {
    if ([string]::IsNullOrWhiteSpace($FixtureSource)) {
        Write-Output "FATAL: no fixture found and no source clip to derive one from."
        Write-Output "       Place a clip at models\jinguling.wav or pass -FixtureSource PATH."
        exit 2
    }
    & python "$repo\eng\matrix\tools\make-fixtures.py" --src $FixtureSource --outdir $A
    if ($LASTEXITCODE -ne 0) { Write-Output "FATAL: fixture generation failed"; exit 2 }
}
Write-Output "asr/vad fixture : $wav ($([math]::Round((Get-Item $wav).Length/1MB,2)) MiB)"
Write-Output "tts clone ref   : $ttsRef ($((Get-Item $ttsRef).Length) bytes)"

# --- preflight ----------------------------------------------------------------
Write-Step "preflight"
$missing = 0
foreach ($path in @($wav, $ttsRef, $asrModel, $ttsModel, $silero, $boring)) {
    if (-not (Test-Path $path)) { Write-Output "FATAL: missing $path"; $missing++ }
}
if ($missing -gt 0) {
    Write-Output "       Models are gitignored: install them with"
    Write-Output "       dotnet run --project src\AudioCpp.NET.Console -- models download <id> --models-dir models"
    exit 2
}
Write-Output "cmake : $((& cmake --version | Select-Object -First 1))"
Write-Output "dotnet: $((& dotnet --version))"

# --- per-backend matrix -------------------------------------------------------
foreach ($backend in $Backends) {
    $out = "$A\matrix-out\win-$backend"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $bd = "$repo\build\native-$backend"

    if ($Configure -or -not (Test-Path "$bd\CMakeCache.txt")) {
        Write-Step "configure $backend (full set)"
        # Start from an empty tree, mirroring the Linux script. A CMake cache pins the
        # absolute source and binary directories, so a build tree that was created under
        # a different name (or a different checkout) fails with
        # "CMakeCache.txt directory ... is different than the directory ... where
        # CMakeCache.txt was created" and leaves the rest of the matrix running against
        # a half-configured tree.
        Remove-Item $bd -Recurse -Force -ErrorAction SilentlyContinue
        $cmakeArgs = @(
            "-S", ".", "-B", $bd, "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Release",
            "-DAUDIOCPP_SRC=$src",
            "-DAUDIOCPP_BACKEND=$backend",
            "-DAUDIOCPP_MODEL_SET=full",
            "-DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON",
            "-DFETCHCONTENT_SOURCE_DIR_AUDIOCPP_BORINGSSL=$boring"
        )
        if ($backend -eq "cuda") {
            $cmakeArgs += "-DCUDAToolkit_ROOT=C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.3"
            $cmakeArgs += "-DCMAKE_CUDA_ARCHITECTURES=89"
        }
        & cmake @cmakeArgs *> "$A\configure-win-full-$backend.log"
        if ($LASTEXITCODE -ne 0) {
            Mark-Failed "configure-$backend"
            Get-Content "$A\configure-win-full-$backend.log" -Tail 25
            continue
        }
        Select-String -Path "$A\configure-win-full-$backend.log" -Pattern "model composite" |
            Select-Object -First 1 | ForEach-Object { $_.Line }
    }

    $loaders = (Get-Content "$bd\audio_cpp\generated\model_registry_loaders.inc" |
        Select-String -Pattern "make_" -AllMatches).Count
    Write-Output "loaders: $loaders"

    Write-Step "build $backend"
    $jobs = if ($backend -eq "cuda") { $CudaJobs } else { $CpuJobs }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & cmake --build $bd --parallel $jobs `
        --target audiocpp_dotnet_native audiocpp_dotnet_abi_smoke audiocpp_dotnet_e2e `
        *> "$A\build-win-full-$backend.log"
    $rc = $LASTEXITCODE
    $sw.Stop()
    if ($rc -ne 0) {
        Mark-Failed "build-$backend"
        Get-Content "$A\build-win-full-$backend.log" -Tail 30
        continue
    }
    Write-Output ("BUILD OK ({0:n1}s)" -f $sw.Elapsed.TotalSeconds)

    $shim  = "$bd\audiocpp_dotnet_native.dll"
    $smoke = "$bd\audiocpp_dotnet_abi_smoke.exe"
    $e2e   = "$bd\audiocpp_dotnet_e2e.exe"
    Write-Output "shim: $shim ($([math]::Round((Get-Item $shim).Length/1MB,1)) MiB)"

    Write-Step "abi smoke ($backend)"
    & $smoke *> "$out\abi-smoke.log"
    $rc = $LASTEXITCODE
    Get-Content "$out\abi-smoke.log" | Select-Object -Last 6
    Write-Output "abi_smoke exit=$rc"
    if ($rc -ne 0) { Mark-Failed "abi-smoke-$backend" }

    Write-Step "e2e asr ($backend)"
    & $e2e asr $asrModel $wav --family citrinet_asr --backend $backend *> "$out\e2e-asr.log"
    $rc = $LASTEXITCODE
    Get-Content "$out\e2e-asr.log" | Select-Object -Last 3
    Write-Output "e2e asr exit=$rc"
    if ($rc -ne 0) { Mark-Failed "e2e-asr-$backend" }

    Write-Step "e2e vad ($backend)"
    & $e2e vad $silero $wav --family silero_vad --backend $backend *> "$out\e2e-vad.log"
    $rc = $LASTEXITCODE
    Get-Content "$out\e2e-vad.log" | Select-Object -Last 3
    Write-Output "e2e vad exit=$rc"
    if ($rc -ne 0) { Mark-Failed "e2e-vad-$backend" }

    Write-Step "e2e tts ($backend)"
    & $e2e tts $ttsModel "Hello from the Windows full-set matrix." "$out\tts.wav" `
        --voice-ref $ttsRef --ref-text "Jing Ling Ke Ji" --max-tokens $maxTok `
        --family qwen3_tts --backend $backend *> "$out\e2e-tts.log"
    $rc = $LASTEXITCODE
    Get-Content "$out\e2e-tts.log" | Select-Object -Last 3
    Write-Output "e2e tts exit=$rc"
    if ($rc -ne 0) { Mark-Failed "e2e-tts-$backend" }
    if (-not (Test-Path "$out\tts.wav") -or (Get-Item "$out\tts.wav").Length -eq 0) {
        Mark-Failed "tts-wav-$backend"
    }

    Write-Step "managed tests against the full $backend shim"
    $env:AUDIOCPP_TEST_NATIVE = $shim
    & dotnet test "$repo\tests\AudioCpp.NET.Tests" --nologo -v q *> "$out\managed-tests.log"
    $rc = $LASTEXITCODE
    Get-Content "$out\managed-tests.log" | Select-Object -Last 4
    if ($rc -ne 0) { Mark-Failed "managed-tests-$backend" }
    Remove-Item Env:AUDIOCPP_TEST_NATIVE -ErrorAction SilentlyContinue
}

Write-Step "ALL DONE (FAILED=$failed)"
Write-Output "MATRIX_FAILED=$failed"
