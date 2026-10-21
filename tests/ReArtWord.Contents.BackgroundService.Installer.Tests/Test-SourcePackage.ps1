#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $temporary ('clutch-source-package-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'repository'
[void][IO.Directory]::CreateDirectory((Join-Path $source 'installer'))
[void][IO.Directory]::CreateDirectory((Join-Path $source 'docs/development'))

function Invoke-Git([string[]] $Arguments) {
    $output = & git -C $source @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
    return $output
}
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid source packaging was accepted.' }
}
try {
    $script = Join-Path $source 'installer/New-SourcePackage.ps1'
    Copy-Item -LiteralPath (Join-Path $repository 'installer/New-SourcePackage.ps1') -Destination $script
    [IO.File]::WriteAllText((Join-Path $source 'docs/development/build.md'), 'Build instructions')
    [IO.File]::WriteAllText((Join-Path $source 'obsolete.txt'), 'Only in earlier history')
    [IO.File]::WriteAllText((Join-Path $source '.gitignore'), '.work/' + [Environment]::NewLine)
    Invoke-Git @('init', '-q') | Out-Null
    Invoke-Git @('add', '.') | Out-Null
    Invoke-Git @('-c', 'user.name=Clutch-CS test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture: earlier source') | Out-Null
    Invoke-Git @('rm', 'obsolete.txt') | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'current.txt'), 'Current source')
    Invoke-Git @('add', '.') | Out-Null
    Invoke-Git @('-c', 'user.name=Clutch-CS test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture: release source') | Out-Null
    [void][IO.Directory]::CreateDirectory((Join-Path $source '.work'))
    [IO.File]::WriteAllText((Join-Path $source '.work/local.txt'), 'Untracked build cache')
    $revision = (Invoke-Git @('rev-parse', 'HEAD')).Trim()
    $output = Join-Path $fixture 'release'
    & $script -OutputDirectory $output -ReleaseId v1.2.3 -Revision $revision | Out-Null
    $archive = Join-Path $output 'Clutch-CS-Source-v1.2.3.zip'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = @($zip.Entries.FullName)
        if ($names -contains 'Clutch-CS-Source-v1.2.3/obsolete.txt' -or @($names | Where-Object { $_ -match '/(\.git|\.work)/' }).Count) { throw 'History or local files were included.' }
        $entry = $zip.GetEntry('Clutch-CS-Source-v1.2.3/current.txt')
        if (-not $entry) { throw 'Current source was omitted.' }
        $reader = [IO.StreamReader]::new($zip.GetEntry('Clutch-CS-Source-v1.2.3/SOURCE-INFO.json').Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.commit -ne $revision -or $metadata.releaseId -ne 'v1.2.3') { throw 'The archive does not identify its binary release.' }
        if (-not $zip.GetEntry('Clutch-CS-Source-v1.2.3/' + $metadata.buildInstructions)) { throw 'The build instructions referenced by the source archive are missing.' }
    } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ((Get-Content -LiteralPath ($archive + '.sha256') -Raw).Trim() -ne "$hash  Clutch-CS-Source-v1.2.3.zip") { throw 'The source checksum differs.' }
    Assert-Rejected { & $script -OutputDirectory $output -ReleaseId v1.2.3 -Revision $revision }
    [IO.File]::WriteAllText((Join-Path $source 'current.txt'), 'Changed after the binary build')
    Assert-Rejected { & $script -OutputDirectory $output -ReleaseId v1.2.4 -Revision $revision }
    Invoke-Git @('restore', 'current.txt') | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'untracked.txt'), 'Uncommitted source')
    Assert-Rejected { & $script -OutputDirectory $output -ReleaseId v1.2.4 -Revision $revision }
    Write-Output 'Source archive contents, revision, checksum and rejection checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($temporary.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'clutch-source-package-*') { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
