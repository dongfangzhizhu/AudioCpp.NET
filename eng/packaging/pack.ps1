# Stage the native shims and pack every publishable project.
#
# Staging is done by eng\packaging\stage-native.ps1 (shared with make-native-archives.ps1)
# so the two entry points can never disagree about where a shim comes from.
#
# Usage:
#   powershell -File eng\packaging\pack.ps1                    # version from Directory.Build.props
#   powershell -File eng\packaging\pack.ps1 -Version 0.2.0
#   powershell -File eng\packaging\pack.ps1 -Backends cpu      # CPU runtime only
#   powershell -File eng\packaging\pack.ps1 -SkipNative        # assume already staged
param(
    [string]$Version = "",
    [string[]]$Backends = @("cpu", "cuda"),
    [string]$OutputDir = "",
    [switch]$SkipNative
)

$ErrorActionPreference = "Continue"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repo

# backend -> runtime package id. One table so adding a backend is a single edit and
# pack.ps1, make-native-archives.ps1 and the archive-name convention cannot drift apart.
$packageForBackend = @{
    cpu    = "AudioCpp.NET.Runtime"
    cuda   = "AudioCpp.NET.Runtime.Cuda"
    vulkan = "AudioCpp.NET.Runtime.Vulkan"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = "$repo\build\nuget" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $SkipNative) {
    & "$repo\eng\packaging\stage-native.ps1" -Backends $Backends
}

$failed = 0
$projects = @("$repo\src\AudioCpp.NET\AudioCpp.NET.csproj")
foreach ($backend in $Backends) {
    if (-not $packageForBackend.ContainsKey($backend)) {
        Write-Output "!!! unknown backend '$backend' (known: $($packageForBackend.Keys -join ', '))"
        exit 2
    }
    $name = $packageForBackend[$backend]
    $projects += "$repo\src\$name\$name.csproj"
}

# Which RIDs actually got staged, so the check below asserts the real set: a partial
# release (e.g. the Linux shim was not built) packages its own contents and reports
# them, instead of failing on a RID that was never going to arrive.
$staging = "$repo\build\nuget-staging"
$rids = @()
foreach ($backend in $Backends) {
    foreach ($rid in @("win-x64", "linux-x64")) {
        $lib = if ($rid -eq "win-x64") { "audiocpp_dotnet_native.dll" } else { "libaudiocpp_dotnet_native.so" }
        if (Test-Path "$staging\$backend\$rid\$lib") { $rids += $rid }
    }
}
$rids = @($rids | Select-Object -Unique)
if ($rids.Count -eq 0) {
    Write-Output "FATAL: no native shim staged under build\nuget-staging -- nothing to package."
    exit 2
}
if ($rids.Count -lt 2) {
    Write-Output "NOTE: only [$($rids -join ', ')] staged; the runtime packages will cover just that platform."
}

foreach ($project in $projects) {
    $name = (Get-Item $project).Directory.Name
    Write-Output ""
    Write-Output "=== pack $name ==="
    # Not `$args`: that is an automatic variable in PowerShell, and assigning to it
    # inside a script silently changes how the script itself binds parameters.
    $packCmd = @("pack", $project, "-c", "Release", "-o", $OutputDir)
    if (-not [string]::IsNullOrWhiteSpace($Version)) { $packCmd += "-p:Version=$Version" }
    & dotnet @packCmd
    if ($LASTEXITCODE -ne 0) { Write-Output "!!! PACK FAILED: $name"; $failed++ }
}

Write-Output ""
Write-Output "=== verifying package layout ==="
# Done in Python, not bash: the PowerShell tool refuses to spawn a non-PowerShell
# shell, and a backslash path handed to bash gets mangled to "D:SouceCode...".
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) {
    Write-Output "!!! python not found on PATH; skipping package verification"
    $failed++
} else {
    & $python "$repo\eng\packaging\verify_packages.py" $OutputDir --natives staged --rids $rids --backends ($Backends -join ",")
    if ($LASTEXITCODE -ne 0) { Write-Output "!!! PACKAGE VERIFICATION FAILED (exit $LASTEXITCODE)"; $failed++ }
}

Write-Output ""
Write-Output "=== packages in $OutputDir ==="
Get-ChildItem "$OutputDir\*.nupkg", "$OutputDir\*.snupkg" -ErrorAction SilentlyContinue |
    Sort-Object Name | ForEach-Object { Write-Output ("  {0,-46} {1,12:N0} B" -f $_.Name, $_.Length) }

Write-Output ""
Write-Output "PACK_FAILED=$failed"
exit $failed
