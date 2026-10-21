#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm', 'linux-arm64', 'osx-arm64')][string] $RuntimeIdentifier = 'win-x64',
    [string] $SourceDirectory,
    [string] $PythonPath = 'python',
    [string] $NinjaPath,
    [string] $VisualStudioPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeSkia.ps1')
$configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build.json') -Raw | ConvertFrom-Json
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'OutputDirectory must be absolute.' }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'native-build.json')) {
    $null = Get-VerifiedSkiaLibrary $OutputDirectory $RuntimeIdentifier
    Write-Output "Using verified Skia native library: $OutputDirectory"
    return
}
$platform, $architecture = $RuntimeIdentifier.Split('-')
$hostPlatform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
if ($platform -ne $hostPlatform -or $architecture -ne [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()) {
    throw 'Build the native library on the target OS and architecture.'
}
if (-not $SourceDirectory) {
    $SourceDirectory = Join-Path ([IO.Path]::GetTempPath()) ("Clutch-CS-native/" + $configuration.commit)
}
$source = [IO.Path]::GetFullPath($SourceDirectory)
$python = (Get-Command $PythonPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
& $python --version
if ($LASTEXITCODE -ne 0) { throw 'Python 3 is required to build Skia.' }
function Invoke-NativeBuildTool([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native build command failed: $File" }
}
if (-not (Test-Path -LiteralPath (Join-Path $source '.git'))) {
    Invoke-NativeBuildTool 'git' @('init', '--quiet', $source)
    Invoke-NativeBuildTool 'git' @('-C', $source, 'config', 'core.longpaths', 'true')
    Invoke-NativeBuildTool 'git' @('-C', $source, 'remote', 'add', 'origin', $configuration.repository)
}
$changes = @(& git -C $source status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'The Skia source checkout has local changes.' }
$current = & git -C $source rev-parse --verify HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or $current -ne $configuration.commit) {
    Invoke-NativeBuildTool 'git' @('-C', $source, 'fetch', '--depth', '1', 'origin', $configuration.commit)
    Invoke-NativeBuildTool 'git' @('-C', $source, 'checkout', '--detach', $configuration.commit)
}
$patch = Join-Path $PSScriptRoot 'msvc-headers.patch'
Invoke-NativeBuildTool 'git' @('-C', $source, 'apply', '--check', $patch)
Invoke-NativeBuildTool 'git' @('-C', $source, 'apply', $patch)
try {
    Invoke-NativeBuildTool $python @((Join-Path $PSScriptRoot 'sync-dependencies.py'), $source)
    $arguments = @(
        'is_official_build=true', 'is_debug=false', 'skia_enable_tools=false',
        'skia_use_dng_sdk=false', 'skia_use_piex=false', 'skia_use_harfbuzz=false', 'skia_use_icu=false', 'skia_use_sfntly=false',
        'skia_use_system_expat=false', 'skia_use_system_libjpeg_turbo=false', 'skia_use_system_libpng=false',
        'skia_use_system_libwebp=false', 'skia_use_system_zlib=false', 'skia_enable_skottie=true',
        ('target_cpu="' + $architecture + '"')
    )
    if ($IsWindows) {
        if (-not $VisualStudioPath) {
            $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
            $VisualStudioPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($LASTEXITCODE -ne 0 -or -not $VisualStudioPath) { throw 'Visual Studio C++ build tools are required.' }
        }
        if (-not $NinjaPath) { $NinjaPath = Join-Path $VisualStudioPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe' }
        $sdk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
        $sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdk 'Lib') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'um/x64/kernel32.lib') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $sdkVersion) { throw 'A Windows SDK with x64 libraries is required.' }
        $arguments += @(
            'target_os="win"', 'skia_enable_fontmgr_win_gdi=false', 'skia_use_vulkan=true', 'skia_use_direct3d=true',
            ('win_vc="' + (Join-Path $VisualStudioPath 'VC').Replace('\','/') + '"'),
            ('win_sdk="' + $sdk.TrimEnd('\').Replace('\','/') + '"'), ('win_sdk_version="' + $sdkVersion.Name + '"'),
            'extra_cflags=["-DSKIA_C_DLL","/MT","/EHsc","/Z7","/guard:cf","-D_HAS_AUTO_PTR_ETC=1"]',
            'extra_ldflags=["/DEBUG:FULL","/DEBUGTYPE:CV,FIXUP","/guard:cf","/DELAYLOAD:d3d12.dll","/DELAYLOAD:D3DCOMPILER_47.dll","/DEFAULTLIB:delayimp"]'
        )
    } else {
        if (-not $NinjaPath) { $NinjaPath = 'ninja' }
        $arguments += @('cc="clang"', 'cxx="clang++"', 'extra_cflags=["-DSKIA_C_DLL"]')
        if ($IsMacOS) {
            $arguments += @('target_os="mac"', 'skia_use_metal=true')
        } else {
            $arguments += @('target_os="linux"', 'skia_use_system_freetype2=false')
        }
    }
    $ninja = (Get-Command $NinjaPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $gn = Join-Path $source $(if ($IsWindows) { 'bin/gn.exe' } else { 'bin/gn' })
    $output = "out/clutch-$RuntimeIdentifier"
    $scriptArgument = "--script-executable=$python"
    Push-Location $source
    try {
        Invoke-NativeBuildTool $gn @('gen', $output, $scriptArgument, ('--args=' + ($arguments -join ' ')))
        foreach ($setting in @('skia_use_dng_sdk', 'skia_use_piex')) {
            $value = & $gn args $output "--list=$setting" --json $scriptArgument
            if ($LASTEXITCODE -ne 0 -or @($value -join [Environment]::NewLine | ConvertFrom-Json)[0].current.value -ne 'false') {
                throw "The generated Skia build enables a prohibited codec: $setting"
            }
        }
        $dependencies = @(& $gn desc $output //:SkiaSharp deps --all $scriptArgument)
        if ($LASTEXITCODE -ne 0 -or ($dependencies -match '(^|/)dng_sdk([/:]|$)|(^|/)piex([/:]|$)')) { throw 'The native build still depends on DNG SDK or piex.' }
        Invoke-NativeBuildTool $ninja @('-C', $output, 'SkiaSharp')
    } finally { Pop-Location }
    $library = if ($IsWindows) { 'libSkiaSharp.dll' } elseif ($IsMacOS) { 'libSkiaSharp.dylib' } else { 'libSkiaSharp.so' }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    $destination = Join-Path $OutputDirectory $library
    Copy-Item -LiteralPath (Join-Path $source "$output/$library") -Destination $destination
    $record = [ordered]@{
        packageVersion=$configuration.packageVersion; sourceCommit=$configuration.commit
        runtimeIdentifier=$RuntimeIdentifier; library=$library
        dngEnabled=$false; piexEnabled=$false; patchSha256=(Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash; sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'native-build.json') -Encoding utf8NoBOM
    $null = Get-VerifiedSkiaLibrary $OutputDirectory $RuntimeIdentifier
    Write-Output "Built Skia without DNG SDK and piex: $destination"
} finally {
    Invoke-NativeBuildTool 'git' @('-C', $source, 'apply', '--reverse', $patch)
}
