[CmdletBinding()]
param([Parameter(Mandatory)][string] $BundleRoot, [Parameter(Mandatory)][string[]] $RelativePayloadPaths)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($BundleRoot) -or -not $RelativePayloadPaths.Count) { throw 'An absolute bundle root and payload directories are required.' }
$root = [IO.Path]::GetFullPath($BundleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$variants = @()
$platforms = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$basis = $null
foreach ($relative in $RelativePayloadPaths) {
    if (-not $relative -or $relative -match '[\\:<>"|?*\x00-\x1f]' -or $relative.StartsWith('/') -or ($relative.Split('/') | Where-Object { -not $_ -or $_ -eq '.' -or $_ -eq '..' })) { throw 'Payload paths must be safe relative directories.' }
    $directory = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $directory.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload escaped bundle root.' }
    $ancestor = $directory
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not permitted.' }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    $manifest = Get-Content -LiteralPath (Join-Path $directory 'release-manifest.json') -Raw | ConvertFrom-Json
    if (-not $manifest.platform -or -not $platforms.Add($manifest.platform)) { throw 'Every manifest requires a unique RID.' }
    if ($null -eq $basis) { $basis = $manifest }
    $expected = [Collections.Generic.HashSet[string]]::new([string[]]$basis.compatibleReleaseIds, [StringComparer]::Ordinal)
    if (-not [string]::Equals($basis.releaseId, $manifest.releaseId, [StringComparison]::Ordinal) -or -not $expected.SetEquals([string[]]$manifest.compatibleReleaseIds)) { throw 'All manifests must share releaseId and compatibility identifiers.' }
    $variants += @{manifest=$manifest;relativePayloadPath=$relative}
}
@{manifest=$basis;variants=$variants} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $root 'release-bundle.json') -Encoding utf8
Write-Output 'Created release-bundle.json. Use its manifest and variants in one fixed-peer update request; payloadRoot is the absolute bundle root.'
