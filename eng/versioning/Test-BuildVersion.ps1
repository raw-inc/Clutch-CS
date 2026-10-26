#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repository ('.work/version-tests/' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
foreach ($name in @('Version.props', 'version.ps1', 'build.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $root
}
# 製品の復元・静的解析から分離し、版の設定を実際の .NET 成果物で確認します。
[IO.File]::WriteAllText((Join-Path $root 'Directory.Build.props'), '<Project><Import Project="Version.props" /></Project>')
[IO.File]::WriteAllText((Join-Path $root 'Directory.Build.targets'), '<Project />')
[IO.File]::WriteAllText((Join-Path $root 'Clutch-CS.slnx'), '<Solution><Project Path="a/Fixture.a.csproj" /><Project Path="b/Fixture.b.csproj" /></Solution>')
foreach ($name in @('a', 'b')) {
    $directory = Join-Path $root $name
    [void][IO.Directory]::CreateDirectory($directory)
    [IO.File]::WriteAllText((Join-Path $directory ('Fixture.' + $name + '.csproj')), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>VersionFixture.' + $name + '</AssemblyName></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $directory 'Value.cs'), 'public static class Value { public static int Number => 1; }')
}
function Assert-Same($Expected, $Actual, [string] $Reason) {
    if ($Expected -cne $Actual) { throw "$Reason Expected '$Expected', actual '$Actual'." }
}
function Invoke-Dotnet([string[]] $Arguments) {
    $output = & dotnet @Arguments 2>&1
    $code = $LASTEXITCODE
    $output | Add-Content -LiteralPath (Join-Path $root 'build.log')
    if ($code -ne 0) { throw ($output | Out-String) }
}
function Assert-Assembly([string] $Name, [string] $Version, [int] $Revision = 0) {
    $assembly = Join-Path $root "$Name/bin/Debug/net10.0/VersionFixture.$Name.dll"
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    $numeric = ($Version -split '-')[0] + ".$Revision"
    Assert-Same $numeric $info.FileVersion 'File version.'
    Assert-Same $numeric ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString()) 'Assembly version.'
    Assert-Same $Version ($info.ProductVersion -split '\+')[0] 'Product version.'
    return $info.ProductVersion
}
$versionFile = Join-Path $root 'Version.props'
$changeVersion = Join-Path $root 'version.ps1'
$initialHash = (Get-FileHash -LiteralPath $versionFile).Hash
$initialVersion = & $changeVersion show
$initialBase = [version]($initialVersion -split '-')[0]
[xml]$initialProperties = Get-Content -LiteralPath $versionFile -Raw
$initialRevision = [int]$initialProperties.Project.PropertyGroup.VersionRevision
$solution = Join-Path $root 'Clutch-CS.slnx'
$projectA = Join-Path $root 'a/Fixture.a.csproj'
Invoke-Dotnet @('restore', $solution, '--nologo')
Invoke-Dotnet @('build', $projectA, '--no-restore', '--nologo')
$first = Assert-Assembly 'a' $initialVersion $initialRevision
Invoke-Dotnet @('build', $projectA, '--no-restore', '--nologo', '-t:Rebuild')
Assert-Same $first (Assert-Assembly 'a' $initialVersion $initialRevision) 'Rebuilding must preserve the version.'

# CIと配布スクリプトが同じ公開版を明示しても、内部修正番号を失いません。
Invoke-Dotnet @('build', $solution, '--no-restore', '--nologo', "-p:Version=$initialVersion")
$productA = Assert-Assembly 'a' $initialVersion $initialRevision
Assert-Same $productA (Assert-Assembly 'b' $initialVersion $initialRevision) 'Explicit release version preserves the revision.'

# 配布時の明示値をソリューション内の全プロジェクトへ適用し、元の設定ファイルは維持します。
Invoke-Dotnet @('build', $solution, '--no-restore', '--nologo', '-p:Version=2.3.4-rc.1')
$productA = Assert-Assembly 'a' '2.3.4-rc.1'
Assert-Same $productA (Assert-Assembly 'b' '2.3.4-rc.1') 'Component versions must agree.'
Assert-Same $initialHash (Get-FileHash -LiteralPath $versionFile).Hash 'Building must not edit Version.props.'

# VersionSuffix と通常の build.ps1 の組み合わせを確認します。
$text = [IO.File]::ReadAllText($versionFile)
$text = [regex]::Replace($text, '<VersionSuffix>[^<]*</VersionSuffix>', '<VersionSuffix>preview.1</VersionSuffix>')
[IO.File]::WriteAllText($versionFile, $text)
$preview = $initialBase.ToString(3) + '-preview.1'
Assert-Same $preview (& $changeVersion show) 'Manual prerelease suffix.'
$wrapperOutput = & (Join-Path $root 'build.ps1') -NoRestore 2>&1
if ($LASTEXITCODE -ne 0) { throw ($wrapperOutput | Out-String) }
$wrapperOutput | Add-Content -LiteralPath (Join-Path $root 'build.log')
$null = Assert-Assembly 'a' $preview $initialRevision
$null = Assert-Assembly 'b' $preview $initialRevision

$null = & $changeVersion patch
$patch = "$($initialBase.Major).$($initialBase.Minor).$($initialBase.Build + 1)"
Assert-Same $patch (& $changeVersion show) 'Patch increment clears the suffix.'
Invoke-Dotnet @('build', $projectA, '--no-restore', '--nologo')
$null = Assert-Assembly 'a' $patch
$null = & $changeVersion minor
Assert-Same "$($initialBase.Major).$($initialBase.Minor + 1).0" (& $changeVersion show) 'Minor resets patch.'
$null = & $changeVersion major
Assert-Same "$($initialBase.Major + 1).0.0" (& $changeVersion show) 'Major resets minor and patch.'

foreach ($invalidRevision in @('-1', '65535', '1.5')) {
    $validText = [IO.File]::ReadAllText($versionFile)
    [IO.File]::WriteAllText($versionFile, [regex]::Replace($validText, '<VersionRevision>[^<]*</VersionRevision>', "<VersionRevision>$invalidRevision</VersionRevision>"))
    $beforeInvalid = (Get-FileHash -LiteralPath $versionFile).Hash
    $rejected = $false
    try { $null = & $changeVersion show } catch { $rejected = $true }
    Assert-Same $true $rejected 'Invalid revision must be rejected.'
    Assert-Same $beforeInvalid (Get-FileHash -LiteralPath $versionFile).Hash 'Rejected revision must preserve Version.props.'
    [IO.File]::WriteAllText($versionFile, $validText)
}

$text = [regex]::Replace([IO.File]::ReadAllText($versionFile), '<VersionPrefix>[^<]*</VersionPrefix>', '<VersionPrefix>65534.0.0</VersionPrefix>')
[IO.File]::WriteAllText($versionFile, $text)
$beforeOverflow = (Get-FileHash -LiteralPath $versionFile).Hash
$rejected = $false
try { $null = & $changeVersion major } catch { $rejected = $true }
Assert-Same $true $rejected 'Overflow must be rejected.'
Assert-Same $beforeOverflow (Get-FileHash -LiteralPath $versionFile).Hash 'Rejected changes must preserve Version.props.'
Write-Output "Version checks passed. Evidence: $root"
