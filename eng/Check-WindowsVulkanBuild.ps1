[CmdletBinding()]
param([switch]$Strict)
# Read-only preflight for the Vulkan backend on Windows.
#
# The build/run split is the thing to get right: glslc (the shader compiler) and the
# Vulkan headers are BUILD-time requirements, the loader (vulkan-1.dll, shipped by the
# GPU driver) is a RUN-time one. A machine where vulkaninfo works but glslc is missing
# still fails the configure, because ggml-vulkan asks CMake for
# `find_package(Vulkan COMPONENTS glslc REQUIRED)`.
$ErrorActionPreference = 'Continue'; $fail = 0
function Check($Name, [scriptblock]$Test, $Hint) {
  try { $v = & $Test 2>$null; if ($LASTEXITCODE -eq 0 -or $v) { Write-Host "[OK]   $Name $v" -ForegroundColor Green } else { $script:fail++; Write-Host "[FAIL] $Name`n       $Hint" -ForegroundColor Red } }
  catch { $script:fail++; Write-Host "[FAIL] $Name`n       $Hint" -ForegroundColor Red }
}

Write-Host 'Windows Vulkan build environment (read-only check)' -ForegroundColor Cyan

Check 'MSVC compiler (cl)' { cl } 'Run this from Visual Studio Developer PowerShell, or install the Desktop C++ workload.'
Check 'CMake' { cmake --version } 'Install CMake 3.20+ and add it to PATH.'
Check 'Ninja' { ninja --version } 'Install Ninja (Visual Studio ships one under Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja).'

# SDK location: the same lookup order the matrix scripts use.
$sdk = $env:AUDIOCPP_VULKAN_SDK
if (-not $sdk -or -not (Test-Path (Join-Path $sdk 'Bin\glslc.exe'))) { $sdk = $env:VULKAN_SDK }
if (-not $sdk -or -not (Test-Path (Join-Path $sdk 'Bin\glslc.exe'))) {
  foreach ($root in @('C:\VulkanSDK', "$env:LOCALAPPDATA\Programs\VulkanSDK", 'C:\Program Files\VulkanSDK')) {
    if (-not (Test-Path $root)) { continue }
    $hit = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
      Sort-Object Name -Descending |
      Where-Object { Test-Path (Join-Path $_.FullName 'Bin\glslc.exe') } |
      Select-Object -First 1
    if ($hit) { $sdk = $hit.FullName; break }
  }
}

if ($sdk -and (Test-Path (Join-Path $sdk 'Bin\glslc.exe'))) {
  Write-Host "[OK]   Vulkan SDK: $sdk" -ForegroundColor Green
  $glslcVersion = & (Join-Path $sdk 'Bin\glslc.exe') --version 2>$null | Select-Object -First 1
  if ($glslcVersion) { Write-Host "[OK]   glslc: $glslcVersion" -ForegroundColor Green }
  Check 'Vulkan headers' { Test-Path (Join-Path $sdk 'Include\vulkan\vulkan.h') } "Reinstall the SDK; $sdk\Include\vulkan\vulkan.h is missing."
  Check 'Vulkan import library' { Test-Path (Join-Path $sdk 'Lib\vulkan-1.lib') } "Reinstall the SDK; $sdk\Lib\vulkan-1.lib is missing."
} else {
  $script:fail++
  Write-Host "[FAIL] Vulkan SDK (glslc not found)`n       Install it from https://vulkan.lunarg.com/sdk/home (the SDK-only installer is enough; the runtime already ships with the GPU driver), or set AUDIOCPP_VULKAN_SDK." -ForegroundColor Red
}

# The loader comes from the graphics driver, not from the SDK; without it the shim
# cannot load, but it does not block the build.
if (Test-Path "$env:SystemRoot\System32\vulkan-1.dll") {
  $ver = (Get-Item "$env:SystemRoot\System32\vulkan-1.dll").VersionInfo.FileVersion
  Write-Host "[OK]   Vulkan loader: system32\vulkan-1.dll ($ver)" -ForegroundColor Green
} else {
  Write-Host '[WARN] system32\vulkan-1.dll not found; the shim will not load at run time.' -ForegroundColor Yellow
  Write-Host '       Update the GPU driver (NVIDIA / AMD / Intel all ship a Vulkan 1.2+ ICD).' -ForegroundColor Yellow
}

Write-Host "`nResult: $fail issue(s). Configure with -DAUDIOCPP_BACKEND=vulkan$(if ($sdk) { " -DVULKAN_SDK=$sdk" })." -ForegroundColor Cyan
if ($Strict -and $fail) { exit 1 }
exit 0