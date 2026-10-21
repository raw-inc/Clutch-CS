#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputRoot,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $ReleaseId,
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm', 'linux-arm64', 'osx-arm64')]
    [string] $RuntimeIdentifier = 'win-x64',
    [string[]] $CompatibleReleaseIds = @(),
    [string] $Version,
    [string] $RuntimeVersion = '10.0.13',
    [string] $NativeSkiaDirectory
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot)) { throw 'OutputRoot must be absolute.' }
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing packages are never overwritten.' }
$isWindowsPackage = $RuntimeIdentifier.StartsWith('win-')
$package = if ($isWindowsPackage) { Join-Path $output 'files' } else { $output }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repository 'eng/native-skia/NativeSkia.ps1')
if (-not $NativeSkiaDirectory) {
    $NativeSkiaDirectory = Join-Path $repository ".work/native-skia/$RuntimeIdentifier"
    & (Join-Path $repository 'eng/native-skia/Build-NativeSkia.ps1') -OutputDirectory $NativeSkiaDirectory -RuntimeIdentifier $RuntimeIdentifier
}
$nativeSkia = Get-VerifiedSkiaLibrary $NativeSkiaDirectory $RuntimeIdentifier

function Copy-VerifiedSkia {
    $deps = Get-Content -LiteralPath (Join-Path $package 'ReArtWord.Contents.BackgroundService.Installer.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $deps.libraries.ContainsKey('SkiaSharp/' + $nativeSkia.Version)) { throw 'The managed and native Skia versions differ.' }
    Copy-Item -LiteralPath $nativeSkia.LibraryPath -Destination $package -Force
    # DLL を差し替えるため、NuGet 由来の一致しないデバッグシンボルを除く。
    $symbols = Join-Path $package 'libSkiaSharp.pdb'
    if (Test-Path -LiteralPath $symbols) { Remove-Item -LiteralPath $symbols }
    Copy-Item -LiteralPath $nativeSkia.RecordPath -Destination (Join-Path $package 'native-build.json') -Force
    $null = Get-VerifiedSkiaLibrary $package $RuntimeIdentifier
}
if ($Version) {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$') { throw 'Version must be a semantic version.' }
}
$versionProperties = @("-p:RuntimeFrameworkVersion=$RuntimeVersion")
if ($Version) { $versionProperties += "-p:Version=$Version" }
$components = [ordered]@{
    core = 'ReArtWord.Contents.BackgroundService.CoreService'
    updater = 'ReArtWord.Contents.BackgroundService.Updater'
    client = 'ReArtWord.Contents.BackgroundService.ClientHost'
    cli = 'ReArtWord.Contents.BackgroundService.CommandLine'
}
$extension = if ($isWindowsPackage) { '.exe' } else { '' }
$assemblyNames = @{}
$buildVersion = $null
[void][IO.Directory]::CreateDirectory($package)
foreach ($component in $components.GetEnumerator()) {
    $destination = Join-Path $package $component.Key
    $project = Join-Path $repository ('src/' + $component.Value + '/' + $component.Value + '.csproj')
    $assemblyName = & dotnet msbuild $project -nologo -getProperty:AssemblyName @versionProperties
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($assemblyName)) { throw "Could not resolve the assembly name for $($component.Key)." }
    $assemblyNames[$component.Key] = $assemblyName
    # The SDK and the company analyzers run on the build PC; every app carries its target runtime.
    & dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true -o $destination --nologo @versionProperties
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($component.Key). The incomplete output is retained for diagnosis; use a new directory when retrying." }
    $assembly = Join-Path $destination ($assemblyName + '.dll')
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) { throw "The build version is missing for $($component.Key)." }
    if ($null -eq $buildVersion) { $buildVersion = $version }
    if ($version -ne $buildVersion) { throw 'The component build versions differ. Build all components from the same source revision.' }
    if ($component.Key -eq 'core' -and $RuntimeIdentifier -ne 'linux-arm') {
        $captureProject = Join-Path $repository 'src/ReArtWord.Contents.BackgroundService.SignalCapture/ReArtWord.Contents.BackgroundService.SignalCapture.csproj'
        $captureDestination = Join-Path $destination 'tools/signal-capture'
        & dotnet publish $captureProject -c Release -r $RuntimeIdentifier --self-contained true -o $captureDestination --nologo @versionProperties
        if ($LASTEXITCODE -ne 0) { throw 'Native capture worker publish failed. The incomplete package is retained; do not deploy it.' }
        if (-not (Test-Path -LiteralPath (Join-Path $captureDestination ('SignalCapture' + $extension)) -PathType Leaf)) { throw 'The native capture worker executable is missing.' }
    }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $destination
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -PayloadRoot $destination -ReleaseId $ReleaseId -RuntimeIdentifier $RuntimeIdentifier -EntryPoint ($assemblyName + $extension) -ExpectedBuildVersion $version -CompatibleReleaseIds $CompatibleReleaseIds
}
$guiName = 'ReArtWord.Contents.BackgroundService.Installer'
$guiProject = Join-Path $repository "src/$guiName/$guiName.csproj"
& dotnet publish $guiProject -c Release -r $RuntimeIdentifier --self-contained true -o $package --nologo @versionProperties
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed. The incomplete package is retained.' }
Copy-VerifiedSkia
$catalog = Join-Path $repository ('.work/third-party/' + [Guid]::NewGuid().ToString('N') + '.json')
$notices = Join-Path $package 'THIRD-PARTY-NOTICES.txt'
& (Join-Path $PSScriptRoot 'New-ThirdPartyNotices.ps1') -PackageRoot $package -CatalogPath $catalog -NoticesPath $notices -RuntimeVersion $RuntimeVersion
$guiProperties = @("-p:ThirdPartyCatalogPath=$catalog")
if ($isWindowsPackage) { $guiProperties += "-p:SetupLauncherPath=$(Join-Path $output 'Clutch-CS-Setup.exe')" }
& dotnet publish $guiProject -c Release -r $RuntimeIdentifier --self-contained true -o $package --nologo @versionProperties @guiProperties
if ($LASTEXITCODE -ne 0) { throw 'Embedding the third-party notices failed. The incomplete package is retained.' }
Copy-VerifiedSkia
if ($isWindowsPackage) {
    Remove-Item -LiteralPath (Join-Path $package ($guiName + $extension))
} else {
    Move-Item -LiteralPath (Join-Path $package ($guiName + $extension)) -Destination (Join-Path $package ('Clutch-CS-Setup' + $extension))
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $package
foreach ($component in $components.GetEnumerator()) {
    $destination = Join-Path $package $component.Key
    Copy-Item -LiteralPath $notices -Destination $destination
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -PayloadRoot $destination -ReleaseId $ReleaseId -RuntimeIdentifier $RuntimeIdentifier -EntryPoint ($assemblyNames[$component.Key] + $extension) -ExpectedBuildVersion $buildVersion -CompatibleReleaseIds $CompatibleReleaseIds
}
# Each mutable installation carries a verified updater, with identical runtime files deduplicated.
$updaterRoot = Join-Path $package 'updater'
$updaterManifest = Get-Content -LiteralPath (Join-Path $updaterRoot 'release-manifest.json') -Raw | ConvertFrom-Json
foreach ($component in @('core', 'client')) {
    $componentRoot = Join-Path $package $component
    $bundledRoot = Join-Path $componentRoot 'updater'
    [void][IO.Directory]::CreateDirectory($bundledRoot)
    foreach ($file in $updaterManifest.files) {
        $shared = Join-Path $componentRoot $file.path
        if ((Test-Path -LiteralPath $shared -PathType Leaf) -and (Get-FileHash -LiteralPath $shared -Algorithm SHA256).Hash -eq $file.sha256) { continue }
        $target = Join-Path $bundledRoot $file.path
        [void][IO.Directory]::CreateDirectory((Split-Path $target))
        Copy-Item -LiteralPath (Join-Path $updaterRoot $file.path) -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path $updaterRoot 'release-manifest.json') -Destination (Join-Path $bundledRoot 'release-manifest.json')
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -PayloadRoot $componentRoot -ReleaseId $ReleaseId -RuntimeIdentifier $RuntimeIdentifier -EntryPoint ($assemblyNames[$component] + $extension) -ExpectedBuildVersion $buildVersion -CompatibleReleaseIds $CompatibleReleaseIds
}
$coreManifest = Get-Content -LiteralPath (Join-Path $package 'core/release-manifest.json') -Raw | ConvertFrom-Json
$clientManifest = Get-Content -LiteralPath (Join-Path $package 'client/release-manifest.json') -Raw | ConvertFrom-Json
@{manifest=$coreManifest;variants=@(@{manifest=$coreManifest;relativePayloadPath='core'});client=@{manifest=$clientManifest;relativePayloadPath='client'}} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $package 'fleet-release.json') -Encoding utf8
$scripts = Join-Path $package 'installer'
[void][IO.Directory]::CreateDirectory($scripts)
foreach ($name in @('Install-Offline.ps1', 'Install-ServiceActions.ps1', 'Set-WindowsServiceRecovery.ps1', 'Set-WindowsDesktopTrayPreference.ps1', 'Start-Client.ps1', 'Register-ClientStartup.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $scripts $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-readme.md') -Destination (Join-Path $package 'README.md')
Write-Output "Created install package: $output ($RuntimeIdentifier, $buildVersion)."
