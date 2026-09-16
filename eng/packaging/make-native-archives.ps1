# Package the staged native shims as the release archives the publish workflow expects:
#
#   audiocpp-native-win-x64-cpu.zip
#   audiocpp-native-linux-x64-cpu.zip
#   audiocpp-native-win-x64-cuda.zip
#   audiocpp-native-linux-x64-cuda.zip
#
# Each contains exactly one library file and nothing else, so the release job can
# unpack them straight into build/nuget-staging/<backend>/<rid>/.
#
# This is the bridge between "the shims can only be built on a machine with the
# toolchain (and, for CUDA, a GPU)" and "the packages must be assembled by CI". Attach
# the archives to the GitHub Release for the version, then run the release workflow.
#
# Usage:
#   powershell -File eng\packaging\make-native-archives.ps1
#   powershell -File eng\packaging\make-native-archives.ps1 -Backends cpu
param(
    [string[]]$Backends = @("cpu", "cuda"),
    [string]$OutputDir = "",
    [switch]$SkipNative
)

$ErrorActionPreference = "Continue"

$repo = "D:\SouceCode\python2net\audio\audiocpp-dotnet"
Set-Location $repo

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = "$repo\build\native-archives" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $SkipNative) {
    & "$repo\eng\packaging\stage-native.ps1" -Backends $Backends
}

$staging = "$repo\build\nuget-staging"
$made = 0
$skipped = @()

foreach ($backend in $Backends) {
    $suffix = if ($backend -eq "cuda") { "cuda" } else { "cpu" }
    foreach ($rid in @("win-x64", "linux-x64")) {
        $library = if ($rid -eq "win-x64") { "audiocpp_dotnet_native.dll" } else { "libaudiocpp_dotnet_native.so" }
        $source = "$staging\$backend\$rid\$library"
        $archive = "$OutputDir\audiocpp-native-$rid-$suffix.zip"

        if (-not (Test-Path $source)) {
            Write-Output "skip  $(Split-Path $archive -Leaf)  (no $source)"
            $skipped += "$rid/$suffix"
            continue
        }

        Remove-Item $archive -Force -ErrorAction SilentlyContinue
        # Stage into a scratch directory so the archive holds only the library and no
        # directory prefix, which keeps the release job's unpack step trivial.
        $scratch = "$repo\build\native-archives\.scratch"
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        Copy-Item $source $scratch -Force
        Compress-Archive -Path "$scratch\*" -DestinationPath $archive -CompressionLevel Optimal
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue

        Write-Output ("made  {0,-40} {1,12:N0} B  (from {2:N1} MiB)" -f `
            (Split-Path $archive -Leaf), (Get-Item $archive).Length, ((Get-Item $source).Length/1MB))
        $made++
    }
}

Write-Output ""
if ($skipped.Count -gt 0) {
    Write-Output "MISSING: $($skipped -join ', ')"
    Write-Output "         build the missing backends first (eng\matrix scripts) and re-run."
}
Write-Output "archives: $made in $OutputDir"
exit $(if ($made -eq 0) { 1 } else { 0 })
