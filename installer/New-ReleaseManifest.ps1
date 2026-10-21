[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PayloadRoot,
    [Parameter(Mandatory)][string] $ReleaseId,
    [Parameter(Mandatory)][string] $RuntimeIdentifier,
    [Parameter(Mandatory)][string] $EntryPoint,
    [string[]] $CompatibleReleaseIds = @(),
    [string] $ExpectedBuildVersion
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($PayloadRoot)) { throw 'PayloadRoot must be absolute.' }
$root = [IO.Path]::GetFullPath($PayloadRoot)
$files = @()
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue($root)
while ($pending.Count) {
    $current = $pending.Dequeue()
    $item = Get-Item -LiteralPath $current -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A release must not contain symbolic links or reparse points.' }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $current -Force) { $pending.Enqueue($child.FullName) }
        continue
    }
    $relative = [IO.Path]::GetRelativePath($root, $current).Replace('\','/')
    if ($relative -eq 'release-manifest.json') { continue }
    $executable = $relative -eq $EntryPoint
    if (-not $IsWindows) { $executable = $executable -or (([IO.File]::GetUnixFileMode($current) -band [IO.UnixFileMode]::UserExecute) -ne 0) }
    $files += @{path=$relative;size=$item.Length;sha256=(Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash;executable=$executable}
}
if ($EntryPoint -notin $files.path) { throw 'The entry point must exist within the payload.' }
$manifest = @{releaseId=$ReleaseId;platform=$RuntimeIdentifier;entryPoint=$EntryPoint;files=@($files | Sort-Object path);compatibleReleaseIds=$CompatibleReleaseIds;expectedBuildVersion=$ExpectedBuildVersion}
if (-not $ExpectedBuildVersion) { $manifest.expectedBuildVersion = $null }
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'release-manifest.json') -Encoding utf8
Write-Output 'Created release-manifest.json. Compatibility identifiers are operator assertions and must be verified before distribution.'
