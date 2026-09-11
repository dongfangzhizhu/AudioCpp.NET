[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Commit,

    [Parameter()]
    [string] $AudioCppPath = (Join-Path $PSScriptRoot '..\..\audio.cpp')
)

$ErrorActionPreference = 'Stop'
$lockPath = Join-Path $PSScriptRoot 'upstream.lock.json'
$audioCpp = (Resolve-Path $AudioCppPath).Path
& git -C $audioCpp cat-file -e "$Commit^{commit}"
if ($LASTEXITCODE -ne 0) { throw "Commit $Commit is not available in $audioCpp. Fetch it first." }

if (-not $PSCmdlet.ShouldProcess($lockPath, "pin audio.cpp to $Commit after reviewed comparison and successful tests")) { return }

$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
$lock.commit = $Commit.ToLowerInvariant()
$lock.shortCommit = $Commit.Substring(0, 12).ToLowerInvariant()
$lock.branchAtPin = (& git -C $audioCpp branch --show-current).Trim()
$lock.describeAtPin = (& git -C $audioCpp describe --tags --always $Commit).Trim()
$lock.pinnedAtUtc = [DateTime]::UtcNow.ToString('O')
$lock | ConvertTo-Json -Depth 10 | Set-Content $lockPath -Encoding utf8NoBOM
Write-Host "Updated $lockPath. Review and commit the lock change together with shim/test adaptations."