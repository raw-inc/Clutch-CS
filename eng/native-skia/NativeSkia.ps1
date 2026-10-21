#Requires -Version 7.0

function Get-VerifiedSkiaLibrary([string] $Directory, [string] $RuntimeIdentifier) {
    $configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build.json') -Raw | ConvertFrom-Json
    $recordPath = Join-Path $Directory 'native-build.json'
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw 'The Skia native build record is missing.' }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    $patchHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'msvc-headers.patch') -Algorithm SHA256).Hash
    $library = if ($RuntimeIdentifier.StartsWith('win-')) { 'libSkiaSharp.dll' } elseif ($RuntimeIdentifier.StartsWith('osx-')) { 'libSkiaSharp.dylib' } else { 'libSkiaSharp.so' }
    if ($record.packageVersion -ne $configuration.packageVersion -or $record.sourceCommit -ne $configuration.commit -or
        $record.runtimeIdentifier -ne $RuntimeIdentifier -or $record.library -ne $library -or $record.patchSha256 -ne $patchHash -or
        $record.dngEnabled -cne $false -or $record.piexEnabled -cne $false -or $record.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'The Skia native build record does not match the required version, platform or disabled codecs.'
    }
    $path = Join-Path $Directory $library
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $record.sha256) {
        throw 'The Skia native library does not match its build record.'
    }
    return [pscustomobject]@{ LibraryPath=[IO.Path]::GetFullPath($path); RecordPath=[IO.Path]::GetFullPath($recordPath); Version=$record.packageVersion; Record=$record }
}
