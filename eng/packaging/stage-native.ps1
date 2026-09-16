# Stage prebuilt native shims into the layout the runtime packages consume:
#
#   build/nuget-staging/<backend>/win-x64/audiocpp_dotnet_native.dll
#   build/nuget-staging/<backend>/linux-x64/libaudiocpp_dotnet_native.so
#
# Windows shims come from the CMake trees (build/native-<backend>). Linux shims are
# built inside WSL, so they are taken from the matrix output the Linux script leaves in
# the repo, falling back to pulling them directly out of the WSL build tree over the
# /mnt/d mount.
#
# Staging is additive per RID: run this on Windows and on Linux against a shared
# checkout and the resulting package carries both win-x64 and linux-x64 assets.
#
# Usage:
#   powershell -File eng\packaging\stage-native.ps1
#   powershell -File eng\packaging\stage-native.ps1 -Backends cpu
param(
    [string[]]$Backends = @("cpu", "cuda"),
    [string]$Staging = ""
)

$ErrorActionPreference = "Continue"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($Staging)) { $Staging = "$repo\build\nuget-staging" }

# Must match AUDIOCPP_MATRIX_ROOT in eng/matrix/linux-full-matrix.sh.
$wslRoot = if ($env:AUDIOCPP_MATRIX_ROOT) { $env:AUDIOCPP_MATRIX_ROOT } else { "/home/$env:USERNAME/audio-matrix" }

# Translate a Windows path under this checkout into its /mnt/<drive>/ WSL equivalent.
function ConvertTo-WslPath([string]$WindowsPath) {
    $full = (Resolve-Path $WindowsPath -ErrorAction SilentlyContinue).Path
    if (-not $full) { $full = $WindowsPath }
    if ($full -match '^([A-Za-z]):\\(.*)$') {
        return "/mnt/$($Matches[1].ToLower())/$($Matches[2] -replace '\\', '/')"
    }
    return $full -replace '\\', '/'
}

function Invoke-Stage([string]$backend) {
    $dest = "$Staging\$backend"
    New-Item -ItemType Directory -Force -Path "$dest\win-x64", "$dest\linux-x64" | Out-Null

    # --- Windows -------------------------------------------------------------
    $winSrc = "$repo\build\native-$backend\audiocpp_dotnet_native.dll"
    if (Test-Path $winSrc) {
        Copy-Item $winSrc "$dest\win-x64\" -Force
        Write-Output ("  win-x64   : {0,7:N1} MiB  $winSrc" -f ((Get-Item $winSrc).Length/1MB))
    } else {
        Remove-Item "$dest\win-x64" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Output "  win-x64   : (absent) build it with eng\matrix\configure-win.ps1 -Backend $backend && build-win.ps1"
    }

    # --- Linux ---------------------------------------------------------------
    $linuxName = "libaudiocpp_dotnet_native.so"
    $fromMatrix = "$repo\build\artifacts\matrix-out\linux-$backend\$linuxName"
    if (Test-Path $fromMatrix) {
        Copy-Item $fromMatrix "$dest\linux-x64\" -Force
        Write-Output ("  linux-x64 : {0,7:N1} MiB  $fromMatrix" -f ((Get-Item $fromMatrix).Length/1MB))
        return
    }

    # Fall back to the WSL build tree; WSL can write back through /mnt/<drive>.
    $wslSrc = "$wslRoot/build-linux-$backend/$linuxName"
    $wslDest = "$(ConvertTo-WslPath $Staging)/$backend/linux-x64/$linuxName"
    & wsl.exe -d Debian -- bash -c "test -f '$wslSrc' && cp '$wslSrc' '$wslDest' && echo COPIED || echo MISSING" 2>$null | Out-Null
    if (Test-Path "$dest\linux-x64\$linuxName") {
        Write-Output ("  linux-x64 : {0,7:N1} MiB  $wslSrc (from WSL)" -f ((Get-Item "$dest\linux-x64\$linuxName").Length/1MB))
    } else {
        Remove-Item "$dest\linux-x64" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Output "  linux-x64 : (absent) run eng/matrix/linux-full-matrix.sh in WSL"
    }
}

foreach ($backend in $Backends) {
    Write-Output "staging native shims: $backend"
    Invoke-Stage $backend
}
