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
elseif ($Backend -eq "vulkan") {
    # ggml-vulkan compiles its shaders at build time, so the SDK is required here
    # (not just the loader the driver provides). Resolve it the same way
    # win-full-matrix.ps1 does: AUDIOCPP_VULKAN_SDK, then VULKAN_SDK, then the
    # default install locations.
    $sdk = $env:AUDIOCPP_VULKAN_SDK
    if (-not $sdk -or -not (Test-Path (Join-Path $sdk "Bin\glslc.exe"))) { $sdk = $env:VULKAN_SDK }
    if (-not $sdk -or -not (Test-Path (Join-Path $sdk "Bin\glslc.exe"))) {
        foreach ($root in @("C:\VulkanSDK", "$env:LOCALAPPDATA\Programs\VulkanSDK", "C:\Program Files\VulkanSDK")) {
            if (-not (Test-Path $root)) { continue }
            $hit = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                Where-Object { Test-Path (Join-Path $_.FullName "Bin\glslc.exe") } |
                Select-Object -First 1
            if ($hit) { $sdk = $hit.FullName; break }
        }
    }
    if (-not $sdk -or -not (Test-Path (Join-Path $sdk "Bin\glslc.exe"))) {
        Write-Output "FATAL: backend 'vulkan' needs the Vulkan SDK (glslc). Install it from"
        Write-Output "       https://vulkan.lunarg.com/sdk/home or set AUDIOCPP_VULKAN_SDK."
        exit 2
    }
    Write-Output "VULKAN_SDK=$sdk"
    # VULKAN_SDK alone is NOT enough: CMake's FindVulkan does not read it (CMake 4.4
    # reports it as an unused variable), and it only looks for glslc in a component
    # layout the SDK installer never writes. Naming the three pieces explicitly is what
    # actually satisfies `find_package(Vulkan COMPONENTS glslc REQUIRED)`.
    $cmakeArgs += "-DVulkan_INCLUDE_DIR=$sdk\Include"
    $cmakeArgs += "-DVulkan_LIBRARY=$sdk\Lib\vulkan-1.lib"
    $cmakeArgs += "-DVulkan_GLSLC_EXECUTABLE=$sdk\Bin\glslc.exe"
    # The shader compiler and headers must also be reachable for the compile itself,
    # not just for find_package.
    $env:INCLUDE = "$sdk\Include;$env:INCLUDE"
    $env:LIB     = "$sdk\Lib;$env:LIB"
    $env:PATH    = "$sdk\Bin;$env:PATH"
}

& cmake @cmakeArgs
$code = $LASTEXITCODE
Write-Output "CONFIGURE_EXIT=$code"
# Deliberately no `exit`: the caller may be a long-lived host session.
