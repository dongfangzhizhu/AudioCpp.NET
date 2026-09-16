# Configure one Windows backend with the FULL model set (all 71 model targets).
# Uses the VS Developer environment + Ninja so no cmd.exe is involved.
param(
    [string]$Backend = "cpu",
    [string]$BuildDir = ""
)

$ErrorActionPreference = "Stop"

# Visual Studio install to enter. Override with AUDIOCPP_VS when yours differs.
$vs = if ($env:AUDIOCPP_VS) { $env:AUDIOCPP_VS } else { "C:\Program Files\Microsoft Visual Studio\18\Professional" }
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
# Pinned upstream checkout. Override with AUDIOCPP_UPSTREAM when it lives elsewhere.
$src = if ($env:AUDIOCPP_UPSTREAM) { $env:AUDIOCPP_UPSTREAM } else { (Resolve-Path (Join-Path $repo "..\audio-pinned") -ErrorAction SilentlyContinue).Path }
if ([string]::IsNullOrWhiteSpace($src)) { $src = (Join-Path $repo "..\audio-pinned") }
# Offline BoringSSL source cache: FetchContent reaching github.com is unreliable
# here, so the tree is kept under build\deps and reused via FETCHCONTENT_SOURCE_DIR.
$boring = "$repo\build\deps\boringssl-src"
# build\native-<backend> matches the NativeLibraryLoader repository probe
# (build/native*/<library>), so an in-repo `dotnet run` resolves it with no config.
if ([string]::IsNullOrWhiteSpace($BuildDir)) { $BuildDir = "$repo\build\native-$Backend" }

# Duplicate proxy variables crash MSBuild's CL task with a case-insensitive
# dictionary collision; Ninja does not care, but keep the environment clean.
Remove-Item Env:http_proxy -ErrorAction SilentlyContinue
Remove-Item Env:https_proxy -ErrorAction SilentlyContinue

Set-Location $repo

$cmakeArgs = @(
    "-S", ".",
    "-B", $BuildDir,
    "-G", "Ninja",
    "-DCMAKE_BUILD_TYPE=Release",
    "-DAUDIOCPP_SRC=$src",
    "-DAUDIOCPP_BACKEND=$Backend",
    "-DAUDIOCPP_MODEL_SET=full",
    "-DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON",
    "-DFETCHCONTENT_SOURCE_DIR_AUDIOCPP_BORINGSSL=$boring"
)
if ($Backend -eq "cuda") {
    $cmakeArgs += "-DCUDAToolkit_ROOT=C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.3"
    $cmakeArgs += "-DCMAKE_CUDA_ARCHITECTURES=89"
}

& cmake @cmakeArgs
$code = $LASTEXITCODE
Write-Output "CONFIGURE_EXIT=$code"
# Deliberately no `exit`: the caller may be a long-lived host session.
