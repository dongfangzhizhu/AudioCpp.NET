[CmdletBinding()]
param(
    [Parameter()]
    [string] $Candidate = 'origin/main',

    [Parameter()]
    [string] $AudioCppPath = (Join-Path $PSScriptRoot '..\..\audio.cpp'),

    [Parameter()]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$lock = Get-Content (Join-Path $PSScriptRoot 'upstream.lock.json') -Raw | ConvertFrom-Json
$audioCpp = (Resolve-Path $AudioCppPath).Path

function Invoke-Git([Parameter(Mandatory)][string[]] $Arguments) {
    $output = & git -C $audioCpp @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $output"
    }
    return @($output)
}

$pinned = [string]$lock.commit
$candidateCommit = ([string](Invoke-Git @('rev-parse', "$Candidate^{commit}") | Select-Object -First 1)).Trim()
Invoke-Git @('cat-file', '-e', "$pinned^{commit}") | Out-Null
$changed = @(Invoke-Git @('diff', '--name-status', '--find-renames', $pinned, $candidateCommit))
$commits = @(Invoke-Git @('log', '--oneline', "$pinned..$candidateCommit"))

$categories = [ordered]@{
    RuntimeContract = @()
    NativeBuild = @()
    ModelCatalog = @()
    Dependencies = @()
    Tests = @()
    Documentation = @()
    Other = @()
}

foreach ($line in $changed) {
    $path = (($line -split "`t")[-1]).Replace('\', '/')
    switch -Regex ($path) {
        '^include/engine/framework/(runtime|core)/|^src/framework/runtime/' { $categories.RuntimeContract += $line; continue }
        '(^|/)CMakeLists\.txt$|^scripts/build_|^\.github/workflows/' { $categories.NativeBuild += $line; continue }
        '^model_specs/|^include/engine/(models|community_models)/|^src/(models|community_models)/' { $categories.ModelCatalog += $line; continue }
        '^external/|^\.gitmodules$' { $categories.Dependencies += $line; continue }
        '^tests/' { $categories.Tests += $line; continue }
        '^docs/|(^|/)README\.md$' { $categories.Documentation += $line; continue }
        default { $categories.Other += $line }
    }
}

$recommendations = [System.Collections.Generic.List[string]]::new()
if ($categories.RuntimeContract.Count -gt 0) {
    $recommendations.Add('BLOCK automatic pin update: review and adapt the native shim, then run ABI, native, managed, and model E2E tests.')
}
if ($categories.NativeBuild.Count -gt 0 -or $categories.Dependencies.Count -gt 0) {
    $recommendations.Add('Rebuild every supported RID/backend; inspect dynamic dependencies, exported symbols, licenses, SBOM, and portable CPU settings.')
}
if ($categories.ModelCatalog.Count -gt 0) {
    $recommendations.Add('Re-evaluate the compiled model set and capability matrix; add or update model-specific golden tests where affected.')
}
if ($categories.Tests.Count -gt 0) {
    $recommendations.Add('Map upstream test changes to wrapper regression tests; do not assume passing compilation preserves semantics.')
}
if ($changed.Count -eq 0) {
    $recommendations.Add('No source difference. No lock or package version change is required.')
}
elseif ($recommendations.Count -eq 0) {
    $recommendations.Add('Low-risk surface only, but still build the native shim and run the managed test suite before updating the pin.')
}

$report = [System.Collections.Generic.List[string]]::new()
$report.Add('# audio.cpp upstream difference report')
$report.Add('')
$report.Add("- Generated: $([DateTime]::UtcNow.ToString('O'))")
$report.Add("- Repository: $($lock.repository)")
$report.Add("- Pinned: ``$pinned``")
$report.Add("- Candidate: ``$candidateCommit`` ($Candidate)")
$report.Add("- Commits forward: $($commits.Count)")
$report.Add("- Changed paths: $($changed.Count)")
$report.Add('')
$report.Add('## Recommended actions')
$report.Add('')
foreach ($item in $recommendations) { $report.Add("- $item") }
$report.Add('')
$report.Add('## Commits')
$report.Add('')
if ($commits.Count -eq 0) { $report.Add('_None_') } else { foreach ($item in $commits) { $report.Add("- ``$item``") } }

foreach ($entry in $categories.GetEnumerator()) {
    $report.Add('')
    $report.Add("## $($entry.Key) ($($entry.Value.Count))")
    $report.Add('')
    if ($entry.Value.Count -eq 0) { $report.Add('_None_') } else { $report.Add('```text'); foreach ($item in $entry.Value) { $report.Add($item) }; $report.Add('```') }
}

$text = $report -join [Environment]::NewLine
if ($OutputPath) {
    $fullOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $projectRoot $OutputPath }
    [IO.File]::WriteAllText($fullOutput, $text, [Text.UTF8Encoding]::new($false))
    Write-Host "Report written to $fullOutput"
}
$text
