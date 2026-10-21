#Requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('show', 'major', 'minor', 'patch')][string] $Change = 'show')
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'Version.props'
[xml] $document = Get-Content -LiteralPath $path -Raw
$textVersion = [string] $document.Project.PropertyGroup.VersionPrefix
$suffix = [string] $document.Project.PropertyGroup.VersionSuffix
$current = [version] $textVersion
if ($current.Revision -ne -1 -or $current.Build -lt 0 -or $current.ToString(3) -cne $textVersion -or $current.Major -gt 65534 -or $current.Minor -gt 65534 -or $current.Build -gt 65534) { throw 'Version.props must contain a valid major.minor.patch version.' }
if ($Change -eq 'show') { Write-Output ($current.ToString(3) + $(if ($suffix) { '-' + $suffix })); return }
$major = $current.Major
$minor = $current.Minor
$patch = $current.Build
switch ($Change) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
if ($major -gt 65534 -or $minor -gt 65534 -or $patch -gt 65534) { throw 'Version component overflow; no file was changed.' }
$next = "$major.$minor.$patch"
$text = [IO.File]::ReadAllText($path)
$text = $text.Replace("<VersionPrefix>$($current.ToString(3))</VersionPrefix>", "<VersionPrefix>$next</VersionPrefix>")
$text = $text.Replace("<VersionSuffix>$suffix</VersionSuffix>", '<VersionSuffix></VersionSuffix>')
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
Write-Output "$($current.ToString(3)) -> $next (not committed)"
