#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageRoot,
    [Parameter(Mandatory)][string] $CatalogPath,
    [Parameter(Mandatory)][string] $NoticesPath,
    [string] $RuntimeVersion = '10.0.13'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ThirdParty/Read-Notice.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repository 'eng/native-skia/NativeSkia.ps1')
$nativeRecord = Get-Content -LiteralPath (Join-Path $PackageRoot 'native-build.json') -Raw | ConvertFrom-Json
$nativeSkia = Get-VerifiedSkiaLibrary $PackageRoot $nativeRecord.runtimeIdentifier
$sourceRoot = Join-Path $PSScriptRoot 'ThirdParty'
$web = Join-Path $repository 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot'
$webNotices = @(Get-Content -LiteralPath (Join-Path $web 'licenses/notices.json') -Raw | ConvertFrom-Json)
$rules = @(Get-Content -LiteralPath (Join-Path $sourceRoot 'packages.json') -Raw | ConvertFrom-Json -AsHashtable)
$sources = @{}
foreach ($source in (Get-Content -LiteralPath (Join-Path $sourceRoot 'sources.json') -Raw | ConvertFrom-Json -AsHashtable)) {
    $sources[$source.file] = $source
}
$packageFolders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packageDirectories = @{}
$cache = @{}

function Read-Assets([string] $ProjectName) {
    $path = Join-Path $repository "src/$ProjectName/obj/project.assets.json"
    $assets = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    foreach ($folder in $assets.packageFolders.Keys) { [void]$packageFolders.Add($folder) }
    return $assets
}

function Get-PublishedPackages([string] $Directory, [string] $ProjectName) {
    $null = Read-Assets $ProjectName
    $arguments = @('-nologo', '-getProperty:AssemblyName')
    $assemblyName = & dotnet msbuild (Join-Path $repository "src/$ProjectName/$ProjectName.csproj") @arguments
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($assemblyName)) { throw "Could not resolve the assembly name: $ProjectName" }
    $deps = Get-Content -LiteralPath (Join-Path $Directory "$assemblyName.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    $target = $deps.targets[$deps.runtimeTarget.name]
    if (-not $target) { throw "The published runtime target is missing: $ProjectName" }
    foreach ($entry in $target.GetEnumerator()) {
        if ($deps.libraries[$entry.Key].type -notin @('package', 'runtimepack')) { continue }
        $present = $false
        foreach ($kind in @('runtime', 'native', 'resources')) {
            foreach ($asset in $entry.Value[$kind].Keys) {
                $name = [IO.Path]::GetFileName($asset)
                if ($name -eq '_._') { continue }
                $relative = $name
                if ($kind -eq 'resources') { $relative = Join-Path $entry.Value[$kind][$asset].locale $name }
                if (Test-Path -LiteralPath (Join-Path $Directory $relative) -PathType Leaf) { $present = $true }
            }
        }
        if ($present) { $entry.Key -replace '^runtimepack\.', '' }
    }
}

function Read-VendoredLicense([string] $File) {
    $source = $sources[$File]
    if (-not $source) { throw "License provenance is missing: $File" }
    $path = Join-Path $sourceRoot "licenses/$File"
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $source.sha256) { throw "The license source has changed: $File" }
    return $source.url + "`n`n" + [IO.File]::ReadAllText($path)
}

function Get-License([string] $Identity) {
    if ($cache.ContainsKey($Identity)) { return $cache[$Identity] }
    $name, $version = $Identity.Split('/')
    $directory = $packageDirectories[$Identity]
    if (-not $directory) {
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder $Identity.ToLowerInvariant()
            if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
        }
    }
    if (-not $directory) { throw "The restored package is missing: $Identity" }
    $nuspec = @(Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' -File)
    $metadata = $null
    if ($nuspec.Count -eq 1) { $metadata = ([xml][IO.File]::ReadAllText($nuspec[0].FullName)).package.metadata }
    $license = [string]$metadata.license.InnerText
    $rule = @($rules | Where-Object { $name -like $_.name -and $version -like $_.version } | Select-Object -First 1)
    if ($rule.Count -eq 1 -and $metadata.license.type -eq 'expression' -and $license -ne $rule[0].license) {
        throw "The license expression changed for $Identity. Review ThirdParty/packages.json."
    }
    $texts = [Collections.Generic.List[string]]::new()
    if ($metadata.copyright) { $texts.Add([string]$metadata.copyright) }
    if ($metadata.authors) { $texts.Add('Package authors: ' + [string]$metadata.authors) }
    $licenseFiles = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '^(LICENSE(?:\..*)?|THIRD[-_]?PARTY[-_]?NOTICES(?:\..*)?|NOTICE(?:\..*)?)$' })
    $hasPrimary = @($licenseFiles | Where-Object { $_.Name -match '^LICENSE(?:\.|$)' }).Count -gt 0
    foreach ($file in $licenseFiles | Sort-Object Name) {
        $text = Read-ThirdPartyNotice $file.FullName
        if ($name -like 'SkiaSharp*' -or $name -like 'HarfBuzzSharp*') {
            # 同梱しない二つの依存ライブラリの節だけを除き、残る上流の通知文は保持する。
            foreach ($excluded in @('DNG SDK', 'piex')) {
                $section = [regex]::Escape($excluded)
                $pattern = '(?ms)^#+\r?\n# ' + $section + '\r?\n#+\r?\n.*?^#+\r?\n# END: ' + $section + '\r?\n#+\r?\n?'
                $text = [regex]::Replace($text, $pattern, '')
            }
        }
        $texts.Add("$($file.Name)`n`n" + $text)
    }
    if (-not $hasPrimary) {
        $existing = @($webNotices | Where-Object { $_.name -eq $name -and $_.version -eq $version } | Select-Object -First 1)
        if ($existing.Count -eq 1 -and $existing[0].notices.Count) {
            $texts.Add($existing[0].project)
            foreach ($notice in $existing[0].notices) {
                $texts.Add([IO.File]::ReadAllText((Join-Path $web $notice.href.TrimStart('/'))))
            }
        } else {
            if ($rule.Count -ne 1 -or -not $rule[0].files) { throw "No license text is available for $Identity. Add the upstream text and provenance to ThirdParty/." }
            foreach ($file in $rule[0].files) { $texts.Add((Read-VendoredLicense $file)) }
        }
    }
    if ($rule.Count -eq 1) {
        foreach ($pattern in $rule[0].supplements) {
            $matches = @($sources.Keys | Where-Object { $_ -like $pattern } | Sort-Object)
            if ($matches.Count -eq 0) { throw "The native license supplement is missing: $Identity / $pattern" }
            foreach ($file in $matches) { $texts.Add((Read-VendoredLicense $file)) }
        }
    }
    elseif ($name -like 'SkiaSharp*' -or $name -like 'HarfBuzzSharp*') {
        throw "Review the native licenses for the new package version: $Identity"
    }
    if (-not $license -or $metadata.license.type -eq 'file') {
        $license = if ($rule.Count -eq 1) { $rule[0].license } else { 'License and third-party notices' }
    }
    if ($name -like 'SkiaSharp*') {
        if ($version -ne $nativeSkia.Version) { throw 'The Skia notices do not match the verified native build.' }
        $texts.Insert(0, 'The Clutch-CS native build excludes Adobe DNG SDK and piex. Upstream notices below also cover optional components that are not included in this build.')
    }
    $result = [ordered]@{ Name = $name; Version = $version; License = $license; Text = $texts -join "`n`n" }
    if ($rule.Count -eq 1 -and $rule[0].selectedLicense) { $result.SelectedLicense = $rule[0].selectedLicense }
    if ($rule.licenseSelections) {
        $result.LicenseSelections = @($rule.licenseSelections | ForEach-Object {
            [ordered]@{ Name = $_.name; License = $_.license; SelectedLicense = $_.selectedLicense }
        })
    }
    $cache[$Identity] = $result
    return $result
}

$installer = @(Get-PublishedPackages $PackageRoot 'ReArtWord.Contents.BackgroundService.Installer')
$products = [ordered]@{
    core = 'ReArtWord.Contents.BackgroundService.CoreService'
    updater = 'ReArtWord.Contents.BackgroundService.Updater'
    client = 'ReArtWord.Contents.BackgroundService.ClientHost'
    cli = 'ReArtWord.Contents.BackgroundService.CommandLine'
}
$clutch = @(
    foreach ($product in $products.GetEnumerator()) { Get-PublishedPackages (Join-Path $PackageRoot $product.Key) $product.Value }
)

# Blazorの実配布ファイルから、トリミング後も残るライブラリを取得する。
$clientName = 'ReArtWord.Contents.BackgroundService.Client'
$clientAssets = Read-Assets $clientName
$browserTarget = @($clientAssets.targets.Keys | Where-Object { $_ -like '*/browser-wasm' })
if ($browserTarget.Count -ne 1) { throw 'The restored browser-wasm target is missing or ambiguous.' }
$frameworkFiles = @(Get-ChildItem -LiteralPath (Join-Path $PackageRoot 'client/wwwroot/_framework') -File | Select-Object -ExpandProperty Name)
foreach ($entry in $clientAssets.targets[$browserTarget[0]].GetEnumerator()) {
    if ($entry.Value.type -ne 'package') { continue }
    foreach ($asset in $entry.Value.runtime.Keys) {
        $assembly = [IO.Path]::GetFileNameWithoutExtension($asset)
        if ($frameworkFiles -match ('^' + [regex]::Escape($assembly) + '(?:\.[^.]+)?\.wasm$')) { $clutch += $entry.Key; break }
    }
}

# ワークロードのランタイムにはNuGetキャッシュとは別の通知文書が付属する。
$resolveArguments = @('-nologo', '-t:ResolveFrameworkReferences', '-getItem:ResolvedRuntimePack', '-p:Configuration=Release', "-p:RuntimeFrameworkVersion=$RuntimeVersion")
$runtimeJson = & dotnet msbuild (Join-Path $repository "src/$clientName/$clientName.csproj") @resolveArguments
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the browser runtime notices.' }
$packs = ($runtimeJson -join "`n" | ConvertFrom-Json -AsHashtable).Items.ResolvedRuntimePack
foreach ($pack in $packs) {
    if ($pack.RuntimeIdentifier -ne 'browser-wasm') { continue }
    $identity = $pack.NuGetPackageId + '/' + $pack.NuGetPackageVersion
    $packageDirectories[$identity] = $pack.PackageDirectory
    $clutch += $identity
}
if (-not $packageDirectories.Count) { throw 'The browser runtime license directory is missing.' }

$catalog = [ordered]@{
    Installer = @($installer | Sort-Object -Unique | ForEach-Object { Get-License $_ } | Sort-Object -Culture en-US { $_.Name }, { $_.Version })
    ClutchCs = @($clutch | Sort-Object -Unique | ForEach-Object { Get-License $_ } | Sort-Object -Culture en-US { $_.Name }, { $_.Version })
}
# Web UI と Core に同梱する JavaScript の通知文書を収録する。
$scriptNames = @('xterm.js / addon-fit', '@noble/hashes', 'highlight.js', 'Acorn')
$scriptNames += @(Get-Content -LiteralPath (Join-Path $web 'vendor/code-editor/packages.json') -Raw | ConvertFrom-Json | Select-Object -ExpandProperty name)
foreach ($entry in $webNotices | Where-Object { $_.name -in $scriptNames }) {
    $texts = @($entry.project, $entry.authors)
    foreach ($notice in $entry.notices) {
        $texts += [IO.File]::ReadAllText((Join-Path $web $notice.href.TrimStart('/')))
    }
    $catalog.ClutchCs += [ordered]@{ Name = $entry.name; Version = $entry.version; License = $entry.license; Text = $texts -join "`n`n" }
}
$catalog.ClutchCs = @($catalog.ClutchCs | Sort-Object -Culture en-US { $_.Name }, { $_.Version })
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CatalogPath)))
$catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $CatalogPath -Encoding utf8NoBOM
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('Clutch-CS - Third-party licenses')
foreach ($scope in $catalog.GetEnumerator()) {
    $notices.Add($scope.Key)
    foreach ($entry in $scope.Value) {
        $heading = "$($entry.Name) $($entry.Version)`n$($entry.License)"
        if ($entry.SelectedLicense) { $heading += "`nSelected license: $($entry.SelectedLicense)" }
        foreach ($selection in $entry.LicenseSelections) {
            $heading += "`n$($selection.Name): Selected license: $($selection.SelectedLicense) ($($selection.License))"
        }
        $notices.Add("$heading`n`n$($entry.Text)")
    }
}
[IO.File]::WriteAllText($NoticesPath, $notices -join "`n`n============================================================`n`n")
Write-Output "Third-party licenses: $($catalog.Installer.Count) installer packages, $($catalog.ClutchCs.Count) Clutch-CS packages."
