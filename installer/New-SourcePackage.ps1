#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$')][string] $ReleaseId,
    [string] $Revision = 'HEAD'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'OutputDirectory must be absolute.' }
$commit = & git -C $repository rev-parse --verify --end-of-options "$Revision^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'The source revision could not be resolved.' }
$commit = ($commit -join '').Trim()
if ($commit -notmatch '^[0-9a-f]{40}$') { throw 'The source revision must identify one commit.' }
& git -C $repository diff --quiet $commit --
if ($LASTEXITCODE -ne 0) { throw 'Build release sources from the selected commit without local changes.' }
$untracked = @(& git -C $repository ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0 -or $untracked.Count) { throw 'Untracked source files must be committed or excluded before packaging.' }
$archive = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "Clutch-CS-Source-$ReleaseId.zip"
if ([IO.File]::Exists($archive) -or [IO.File]::Exists($archive + '.sha256')) { throw 'The source archive or checksum already exists.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$prefix = "Clutch-CS-Source-$ReleaseId/"
& git -C $repository archive --format=zip "--prefix=$prefix" "--output=$archive" $commit
if ($LASTEXITCODE -ne 0) { throw 'The source archive could not be created.' }
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.CreateEntry($prefix + 'SOURCE-INFO.json')
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try {
        $writer.WriteLine(([ordered]@{ releaseId=$ReleaseId; commit=$commit; buildInstructions='docs/development/build.md' } | ConvertTo-Json))
    } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))" + [Environment]::NewLine)
Write-Output "Created corresponding source archive: $archive ($commit)"
