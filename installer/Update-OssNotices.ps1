#Requires -Version 7.0
param(
    [string]$NugetRoot = (Join-Path $env:USERPROFILE '.nuget/packages'),
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$RuntimeVersion = '10.0.13'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ThirdParty/Read-Notice.ps1')
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/licenses'
New-Item -ItemType Directory $output -Force | Out-Null
$packages = @{}
$packageDirectories = @{}
$existing = @(Get-Content -LiteralPath (Join-Path $output 'notices.json') -Raw | ConvertFrom-Json -AsHashtable)
$sourceRoot = Join-Path $PSScriptRoot 'ThirdParty'
$rules = @(Get-Content -LiteralPath (Join-Path $sourceRoot 'packages.json') -Raw | ConvertFrom-Json -AsHashtable)
$sources = @{}
foreach ($source in (Get-Content -LiteralPath (Join-Path $sourceRoot 'sources.json') -Raw | ConvertFrom-Json)) { $sources[$source.file] = $source }
foreach ($project in @('Client','ClientHost','CoreService','CommandLine','Updater')) {
    $assets = Get-Content (Join-Path $root "src/ReArtWord.Contents.BackgroundService.$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach ($target in $assets.targets.Values) {
        foreach ($item in $target.GetEnumerator()) {
            if ($item.Value.type -ne 'package') { continue }
            $files = @($item.Value.runtime.Keys) + @($item.Value.runtimeTargets.Keys) + @($item.Value.native.Keys)
            if ($files | Where-Object { $_ -and [IO.Path]::GetFileName($_) -ne '_._' }) {
                $packages[$item.Key] = $true
                foreach ($folder in @($NugetRoot) + @($assets.packageFolders.Keys)) {
                    $candidate = Join-Path $folder $item.Key.ToLowerInvariant()
                    if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectories[$item.Key] = $candidate; break }
                }
            }
        }
    }
}
# SDK 全体の通知ではなく、配布するサーバーとブラウザーのランタイムを収録する。
foreach ($project in @('Client', 'ClientHost')) {
    $arguments = @('-nologo', '-t:ResolveFrameworkReferences', '-getItem:ResolvedRuntimePack',
        '-p:Configuration=Release', "-p:RuntimeFrameworkVersion=$RuntimeVersion")
    if ($project -eq 'ClientHost') { $arguments += @("-p:RuntimeIdentifier=$RuntimeIdentifier", '-p:SelfContained=true') }
    $json = & dotnet msbuild (Join-Path $root "src/ReArtWord.Contents.BackgroundService.$project/ReArtWord.Contents.BackgroundService.$project.csproj") @arguments
    if ($LASTEXITCODE -ne 0) { throw "Could not resolve the runtime licenses for $project." }
    foreach ($pack in (($json -join [Environment]::NewLine | ConvertFrom-Json).Items.ResolvedRuntimePack)) {
        $identity = $pack.NuGetPackageId + '/' + $pack.NuGetPackageVersion
        $packages[$identity] = $true
        $packageDirectories[$identity] = $pack.PackageDirectory
    }
}
$rows = @()
$downloaded = @{}
foreach ($key in ($packages.Keys | Sort-Object)) {
    $parts = $key.Split('/')
    $directory = $packageDirectories[$key]
    if (-not $directory) { throw "The restored package is missing: $key" }
    [xml]$spec = Get-Content (Join-Path $directory ($parts[0].ToLowerInvariant()+'.nuspec')) -Raw
    $meta = $spec.package.metadata
    $license = $meta.license.InnerText
    $rule = $rules | Where-Object { $parts[0] -like $_.name -and $parts[1] -like $_.version } | Select-Object -First 1
    if ($rule -and $meta.license.type -eq 'expression' -and $license -ne $rule.license) {
        throw "The license expression changed for $key. Review ThirdParty/packages.json."
    }
    if ($license -match '\bOR\b' -and -not $rule.selectedLicense) { throw "Select a license for $key in ThirdParty/packages.json." }
    $notices = @()
    $packagedLicense = Get-ChildItem -LiteralPath $directory -File | Where-Object Name -Match '^LICENSE(?:\..*)?$' | Select-Object -First 1
    $previous = $existing | Where-Object { $_.name -eq $parts[0] -and $_.version -eq $parts[1] } | Select-Object -First 1
    if ($meta.license.type -eq 'file' -or $packagedLicense) {
        $name = $parts[0] + '-' + $parts[1] + '-license.txt'
        $file = if ($meta.license.type -eq 'file') { Join-Path $directory $license } else { $packagedLicense.FullName }
        Copy-Item -LiteralPath $file -Destination (Join-Path $output $name) -Force
        $notices += @{label='License';href="/licenses/$name"}
    } elseif ($previous) {
        $notices += $previous.notices
    } elseif (-not $rule.files) {
        $repo = [string]$meta.repository.url
        if (!$repo) { $repo = [string]$meta.projectUrl }
        if ($repo -match '^https://github.com/([^/]+/[^/]+?)(?:\.git)?/?$') {
            $repository = $Matches[1]
            $revision = [string]$meta.repository.commit
            if (-not $revision) { throw "Add a pinned license source for $key to ThirdParty/." }
            $identity = "$repository@$revision"
            if (!$downloaded.ContainsKey($identity)) {
                $uri = "https://api.github.com/repos/$repository/license"
                if ($revision) { $uri += "?ref=$revision" }
                $result = Invoke-RestMethod $uri -Headers @{ 'User-Agent'='Contents-License-Inventory' }
                $name = $parts[0] + '-' + $parts[1] + '-license.txt'
                [IO.File]::WriteAllBytes((Join-Path $output $name),[Convert]::FromBase64String($result.content))
                $downloaded[$identity] = $name
            }
            $notices += @{label='License';href=('/licenses/'+$downloaded[$identity])}
        } else { throw "No packaged or upstream license for $key. Add its authoritative notice before publishing." }
    }
    if ($meta.license.type -eq 'file') {
        if (-not $rule.license) { throw "Review the license name for $key in ThirdParty/packages.json." }
        $license = $rule.license
    }
    foreach ($file in $rule.files) {
        $source = $sources[$file]
        $path = Join-Path $sourceRoot "licenses/$file"
        if (-not $source -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $source.sha256) {
            throw "The license source is missing or changed: $file"
        }
        Copy-Item -LiteralPath $path -Destination (Join-Path $output $file) -Force
        $notices += @{label=[IO.Path]::GetFileNameWithoutExtension($file);href="/licenses/$file"}
    }
    foreach ($file in Get-ChildItem $directory -File | Where-Object Name -Match '^(THIRD[-_]?PARTY[-_]?NOTICES?|NOTICE)(?:\..*)?$') {
        $name = $parts[0]+'-'+$parts[1]+'-third-party.txt'
        [IO.File]::WriteAllText((Join-Path $output $name), (Read-ThirdPartyNotice $file.FullName))
        $notices += @{label='Third-party notices';href="/licenses/$name"}
    }
    $project = [string]$meta.projectUrl
    if (!$project) { $project = ([string]$meta.repository.url) -replace '\.git$', '' }
    $row = [ordered]@{name=$parts[0];version=$parts[1];license=$license;authors=[string]$meta.authors;copyright=[string]$meta.copyright;project=$project;notices=$notices}
    if ($rule.selectedLicense) { $row.selectedLicense = $rule.selectedLicense }
    if ($rule.licenseSelections) { $row.licenseSelections = $rule.licenseSelections }
    $rows += $row
}
Copy-Item (Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/xterm/LICENSE') (Join-Path $output 'xterm-license.txt')
Copy-Item (Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/noble-hashes/LICENSE') (Join-Path $output 'noble-hashes-license.txt')
Copy-Item (Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/highlight/LICENSE') (Join-Path $output 'highlight-license.txt')
$rows += @{name='xterm.js / addon-fit';version='6.0.0 (local IME fix) / 0.11.0';license='MIT';authors='The xterm.js authors';project='https://github.com/xtermjs/xterm.js';notices=@(@{label='License';href='/licenses/xterm-license.txt'})}
$rows += @{name='@noble/hashes';version='2.0.1';license='MIT';authors='Paul Miller';project='https://github.com/paulmillr/noble-hashes';notices=@(@{label='License';href='/licenses/noble-hashes-license.txt'})}
$rows += @{name='highlight.js';version='11.12.0';license='BSD-3-Clause';authors='Josh Goebel and contributors';project='https://highlightjs.org/';notices=@(@{label='License';href='/licenses/highlight-license.txt'})}
Copy-Item (Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/code-editor/LICENSE') (Join-Path $output 'code-editor-license.txt')
foreach ($package in (Get-Content (Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/code-editor/packages.json') -Raw | ConvertFrom-Json)) {
    $rows += @{name=$package.name;version=$package.version;license=$package.license;authors=$package.authors;project=$package.project;notices=@(@{label='License';href='/licenses/code-editor-license.txt'})}
}
$acorn = Join-Path $root 'src/ReArtWord.Contents.BackgroundService.Nodes/Scripting/JavaScript/vendor'
$acornVersion = [regex]::Match([IO.File]::ReadAllText((Join-Path $acorn 'acorn.mjs')), 'var version = "([^"]+)";').Groups[1].Value
if (-not $acornVersion) { throw 'Could not read the bundled Acorn version.' }
Copy-Item -LiteralPath (Join-Path $acorn 'LICENSE') -Destination (Join-Path $output 'acorn-license.txt') -Force
$rows += @{name='Acorn';version=$acornVersion;license='MIT';authors='Acorn contributors';project='https://github.com/acornjs/acorn';notices=@(@{label='License';href='/licenses/acorn-license.txt'})}
$rows = @($rows | Sort-Object -Culture en-US { $_.name }, { $_.version })
# 同一本文は共用し、更新後に参照されない生成ファイルを取り除く。
$byHash = @{}
foreach ($row in $rows) {
    foreach ($notice in $row.notices) {
        $file = Join-Path $output ([IO.Path]::GetFileName($notice.href))
        $text = [IO.File]::ReadAllText($file).Replace([string][char]13, '')
        $text = [regex]::Replace($text, '(?m)[\t ]+$', '').TrimEnd() + [char]10
        [IO.File]::WriteAllText($file, $text)
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if ($byHash.ContainsKey($hash) -and $byHash[$hash] -ne $notice.href) {
            $notice.href = $byHash[$hash]
        } else { $byHash[$hash] = $notice.href }
    }
    $row.notices = @($row.notices | Sort-Object { $_.href } -Unique)
}
$referenced = @($rows.notices.href | ForEach-Object { [IO.Path]::GetFileName($_) })
foreach ($file in (Get-ChildItem -LiteralPath $output -File -Filter '*.txt' | Where-Object { $_.Name -notin $referenced } | Select-Object -ExpandProperty FullName)) {
    if (![IO.Path]::GetFullPath($file).StartsWith([IO.Path]::GetFullPath($output)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated notice path.' }
    Remove-Item -LiteralPath $file
}
$rows = @($rows | ForEach-Object {
    $entry = [ordered]@{name=$_.name;version=$_.version;license=$_.license;authors=$_.authors;project=$_.project}
    if ($_.copyright) { $entry.copyright = $_.copyright }
    if ($_.selectedLicense) { $entry.selectedLicense = $_.selectedLicense }
    if ($_.licenseSelections) { $entry.licenseSelections = $_.licenseSelections }
    $entry.notices = @($_.notices | ForEach-Object { [ordered]@{label=$_.label;href=$_.href} })
    $entry
})
ConvertTo-Json -InputObject $rows -Depth 8 | Set-Content (Join-Path $output 'notices.json') -Encoding utf8NoBOM
Write-Output "Generated notices for $($rows.Count) components from restored runtime assets."
