[CmdletBinding()]
param([switch]$Strict)
$ErrorActionPreference = 'Continue'; $fail = 0
function Check($Name, [scriptblock]$Test, $Hint) { try { $v = & $Test 2>$null; if ($LASTEXITCODE -eq 0 -or $v) { Write-Host "[OK]   $Name $($v -join ' ')" -ForegroundColor Green } else { $script:fail++; Write-Host "[FAIL] $Name`n       $Hint" -ForegroundColor Red } } catch { $script:fail++; Write-Host "[FAIL] $Name`n       $Hint" -ForegroundColor Red } }
Write-Host 'Windows CUDA GPU build environment (read-only check)' -ForegroundColor Cyan
Check 'NVIDIA driver / GPU' { nvidia-smi --query-gpu=name,compute_cap --format=csv,noheader } 'Install/update the NVIDIA driver and confirm the GPU is visible with nvidia-smi.'
Check 'CUDA compiler (nvcc)' { nvcc --version } 'Install a complete CUDA Toolkit and add its bin directory to PATH.'
Check 'MSVC compiler (cl)' { cl } 'Run this script from Visual Studio Developer PowerShell, or install the Desktop C++ workload.'
Check 'MSBuild' { msbuild -version } 'Install Visual Studio/MSBuild and use its Developer PowerShell.'
Check 'CMake' { cmake --version } 'Install CMake 3.20+ and add it to PATH.'
$cuda = $env:CUDA_PATH; if (!$cuda) { Write-Host '[WARN] CUDA_PATH is not set; set it to the selected CUDA Toolkit.' -ForegroundColor Yellow; $fail++ } else { Write-Host "[OK]   CUDA_PATH=$cuda" -ForegroundColor Green }
$roots = @($cuda, 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3', 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8') | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
if (!$roots) { Write-Host '[FAIL] CUDA Toolkit directory not found. Install the CUDA development toolkit.' -ForegroundColor Red; $fail++ } else { foreach($root in $roots) { Check "CUDA headers/runtime in $root" { (Test-Path "$root\include\cuda_runtime.h") -and ((Get-ChildItem "$root\lib\x64\cudart*.lib" -ErrorAction SilentlyContinue).Count -gt 0) } 'Install CUDA Development/Libraries, including cuda_runtime.h and cudart.lib.'; Check "Visual Studio CUDA integration in $root" { Test-Path "$root\extras\visual_studio_integration\MSBuildExtensions" } 'Install the CUDA Visual Studio Integration component.' } }
Write-Host "`nResult: $fail issue(s). Target RTX 4090 architecture is CMAKE_CUDA_ARCHITECTURES=89." -ForegroundColor Cyan
if ($Strict -and $fail) { exit 1 } exit 0