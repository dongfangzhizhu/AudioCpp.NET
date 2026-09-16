# Build one Windows backend build tree inside the VS developer environment.
# Ninja is used for generation, but MSVC still needs INCLUDE/LIB from vcvars,
# which is exactly what Enter-VsDevShell provides without invoking cmd.exe.
param(
    [string]$Backend = "cpu",
    [string]$BuildDir = "",
    [string]$Target = "",
    [int]$Jobs = 32
)

# Native tools write progress to stderr; "Stop" would abort the whole build on the
# first warning line. Exit codes are checked explicitly instead.
$ErrorActionPreference = "Continue"

# Visual Studio install to enter. Override with AUDIOCPP_VS when yours differs.
$vs = if ($env:AUDIOCPP_VS) { $env:AUDIOCPP_VS } else { "C:\Program Files\Microsoft Visual Studio\18\Professional" }
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($BuildDir)) { $BuildDir = "$repo\build\native-$Backend" }

Remove-Item Env:http_proxy -ErrorAction SilentlyContinue
Remove-Item Env:https_proxy -ErrorAction SilentlyContinue

Set-Location $repo

$buildArgs = @("--build", $BuildDir, "--parallel", "$Jobs")
if (-not [string]::IsNullOrWhiteSpace($Target)) { $buildArgs += @("--target", $Target) }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& cmake @buildArgs
$code = $LASTEXITCODE
$sw.Stop()
Write-Output ("BUILD_EXIT={0} elapsed={1:n1}s" -f $code, $sw.Elapsed.TotalSeconds)
