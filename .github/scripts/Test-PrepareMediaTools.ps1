$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('clutch-media-setup-' + [guid]::NewGuid().ToString('N'))
$hostExecutable = (Get-Process -Id $PID).Path
$script = Join-Path $PSScriptRoot 'prepare-media-tools.ps1'
$saved = @{}
foreach ($name in @('PATH', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}

try {
    foreach ($scenario in @('installed', 'download-failed', 'partial', 'broken', 'unstartable', 'installer-missing', 'downloaded')) {
        $directory = Join-Path $root $scenario
        [void][IO.Directory]::CreateDirectory($directory)
        $probe = Join-Path $directory 'ffprobe.cmd'
        $encoder = Join-Path $directory 'ffmpeg.cmd'
        $installer = Join-Path $directory 'choco.cmd'
        if ($scenario -in @('installed', 'partial', 'broken', 'unstartable')) {
            [IO.File]::WriteAllText($probe, "@echo off`r`nexit /b 0`r`n")
        }
        if ($scenario -in @('installed', 'broken')) {
            $code = if ($scenario -eq 'broken') { 7 } else { 0 }
            [IO.File]::WriteAllText($encoder, "@echo off`r`nexit /b $code`r`n")
        }
        if ($scenario -eq 'unstartable') {
            $encoder = Join-Path $directory 'ffmpeg.exe'
            [IO.File]::WriteAllText($encoder, 'This is not an executable.')
        }
        $installBody = "@echo off`r`necho called> `"$directory/install-called`"`r`nexit /b 1`r`n"
        if ($scenario -eq 'downloaded') {
            $installBody = "@echo off`r`necho called> `"$directory/install-called`"`r`n> `"$probe`" echo @exit /b 0`r`n> `"$encoder`" echo @exit /b 0`r`nexit /b 0`r`n"
        }
        if ($scenario -ne 'installer-missing') { [IO.File]::WriteAllText($installer, $installBody) }
        $env:PATH = $directory
        $env:GITHUB_ENV = Join-Path $directory 'environment'
        $env:GITHUB_OUTPUT = Join-Path $directory 'output'
        $env:GITHUB_STEP_SUMMARY = Join-Path $directory 'summary'
        & $hostExecutable -NoProfile -NonInteractive -File $script
        if ($LASTEXITCODE -ne 0) { throw "$scenario did not finish the preparation step." }
        $environment = Get-Content -LiteralPath $env:GITHUB_ENV
        $outputs = Get-Content -LiteralPath $env:GITHUB_OUTPUT
        $expected = $scenario -in @('installed', 'downloaded')
        if ($expected) {
            if ($outputs -ne 'available=true' -or $environment -notcontains 'CONTENTS_TEST_MEDIA_UNAVAILABLE=0' -or
                $environment -notcontains "CONTENTS_TEST_FFMPEG=$encoder" -or $environment -notcontains "CONTENTS_TEST_FFPROBE=$probe") {
                throw "$scenario did not publish both usable tools."
            }
        } else {
            if ($outputs -ne 'available=false' -or $environment -notcontains 'CONTENTS_TEST_MEDIA_UNAVAILABLE=1' -or
                $environment -notcontains 'CONTENTS_TEST_FFMPEG=' -or $environment -notcontains 'CONTENTS_TEST_FFPROBE=' -or
                -not (Test-Path -LiteralPath $env:GITHUB_STEP_SUMMARY)) {
                throw "$scenario did not record unavailable prerequisites."
            }
        }
        $shouldInstall = $scenario -in @('download-failed', 'partial', 'downloaded')
        if ((Test-Path -LiteralPath (Join-Path $directory 'install-called')) -ne $shouldInstall) {
            throw "$scenario used the installer unexpectedly."
        }
        Write-Output "PASS: media setup $scenario"
    }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The temporary test directory is outside the expected root.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
