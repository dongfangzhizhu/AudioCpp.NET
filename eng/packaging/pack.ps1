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

$repo = "D:\SouceCode\python2net\audio\audiocpp-dotnet"
Set-Location $repo

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = "$repo\build\nuget" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $SkipNative) {
    & "$repo\eng\packaging\stage-native.ps1" -Backends $Backends
}

$failed = 0
$projects = @("$repo\src\AudioCpp.NET\AudioCpp.NET.csproj")
foreach ($backend in $Backends) {
    $name = if ($backend -eq "cuda") { "AudioCpp.NET.Runtime.Cuda" } else { "AudioCpp.NET.Runtime" }
    $projects += "$repo\src\$name\$name.csproj"
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
& bash "$repo\eng\packaging\verify-packages.sh" "$OutputDir" "success"

Write-Output ""
Write-Output "=== packages in $OutputDir ==="
Get-ChildItem "$OutputDir\*.nupkg", "$OutputDir\*.snupkg" -ErrorAction SilentlyContinue |
    Sort-Object Name | ForEach-Object { Write-Output ("  {0,-46} {1,12:N0} B" -f $_.Name, $_.Length) }

Write-Output ""
Write-Output "PACK_FAILED=$failed"
exit $failed
