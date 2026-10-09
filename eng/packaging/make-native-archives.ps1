# Package the staged native shims as the release archives the publish workflow expects:
#
#   audiocpp-native-win-x64-cpu.zip
#   audiocpp-native-linux-x64-cpu.zip
#   audiocpp-native-win-x64-cuda.zip
#   audiocpp-native-linux-x64-cuda.zip
#   audiocpp-native-manifest.json   (provenance: pin, shim ABI, per-archive hashes)
#
# Each zip contains exactly one library file and nothing else, so the release job can
# unpack them straight into build/nuget-staging/<backend>/<rid>/. The manifest is the
# fingerprint that lets the release job reject archives built for a different pin:
# without it a new tag silently ships a newer managed package against an older shim.
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

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repo

if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = "$repo\build\native-archives" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $SkipNative) {
    & "$repo\eng\packaging\stage-native.ps1" -Backends $Backends
}

$staging = "$repo\build\nuget-staging"
$made = 0
$skipped = @()

# The archive suffix is the backend name. Kept as an explicit table rather than
# "cuda or cpu" so a new backend can never be silently archived under the CPU name --
# that mistake ships a Vulkan shim labelled "cpu" and nothing detects it downstream.
$archiveSuffix = @{
    cpu    = "cpu"
    cuda   = "cuda"
    vulkan = "vulkan"
}
foreach ($backend in $Backends) {
    if (-not $archiveSuffix.ContainsKey($backend)) {
        Write-Output "FATAL: unknown backend '$backend' (known: $($archiveSuffix.Keys -join ', '))"
        exit 2
    }
}

foreach ($backend in $Backends) {
    $suffix = $archiveSuffix[$backend]
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

# --- provenance manifest ------------------------------------------------------
#
# Archives are binary artefacts that CI cannot rebuild, so they can outlive the
# commit that produced them: attaching the previous release's archives to a new
# tag publishes a managed package built against a newer shim ABI next to a
# runtime package built against the older one, and nothing fails until a consumer
# calls an entry point that only exists in the newer shim. The manifest states
# which pin and ABI these archives were built from; import-native-archives.sh
# (and therefore the release job) refuses to stage them when it disagrees with
# eng/upstream.lock.json.
#
# Upload this file next to the archives.
if ($made -gt 0) {
    $lockPath = Join-Path $PSScriptRoot "..\upstream.lock.json"
    $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
    $entries = @()
    foreach ($backend in $Backends) {
        $suffix = $archiveSuffix[$backend]
        foreach ($rid in @("win-x64", "linux-x64")) {
            $library = if ($rid -eq "win-x64") { "audiocpp_dotnet_native.dll" } else { "libaudiocpp_dotnet_native.so" }
            $source = "$staging\$backend\$rid\$library"
            $archive = "$OutputDir\audiocpp-native-$rid-$suffix.zip"
            if (-not (Test-Path $source) -or -not (Test-Path $archive)) { continue }
            $entries += [ordered]@{
                rid              = $rid
                backend          = $suffix
                archive          = "audiocpp-native-$rid-$suffix.zip"
                library          = $library
                library_bytes    = (Get-Item $source).Length
                library_sha256   = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
                archive_sha256   = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
                archive_bytes    = (Get-Item $archive).Length
            }
        }
    }
    $manifest = [ordered]@{
        schemaVersion         = 1
        audioCppCommit        = $lock.commit.ToLowerInvariant()
        audioCppRepository    = $lock.repository
        shimAbiMajor          = [int]$lock.shimAbi.major
        shimAbiMinor          = [int]$lock.shimAbi.minor
        backendModelSet       = "full"
        builtAtUtc            = [DateTime]::UtcNow.ToString("o")
        archives              = $entries
    }
    $manifestPath = "$OutputDir\audiocpp-native-manifest.json"
    # [System.IO.File]::WriteAllText instead of Set-Content -Encoding utf8NoBOM: the
    # utf8NoBOM encoding name only exists on PowerShell 7+, and this script is also
    # run under Windows PowerShell 5.1. The explicit UTF8Encoding($false) writes
    # BOM-less UTF-8 on both.
    $json = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ""
    Write-Output ("manifest: {0} (pin {1}, shim ABI {2}.{3})" -f `
        (Split-Path $manifestPath -Leaf), $manifest.audioCppCommit.Substring(0, 12), `
        $manifest.shimAbiMajor, $manifest.shimAbiMinor)
}

exit $(if ($made -eq 0) { 1 } else { 0 })
