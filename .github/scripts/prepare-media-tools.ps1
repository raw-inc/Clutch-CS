$ErrorActionPreference = 'Stop'

# 取得不能と試験の失敗を分けるため、利用可否は試験の検出前に確定します。
$probe = Get-Command ffprobe -CommandType Application -ErrorAction SilentlyContinue
$encoder = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue
if (-not $probe -or -not $encoder) {
    try {
        choco install ffmpeg --version=9.0.1 --yes --no-progress
        if ($LASTEXITCODE -ne 0) { Write-Warning "FFmpeg installation failed (exit code $LASTEXITCODE)." }
    } catch [System.Management.Automation.CommandNotFoundException] {
        Write-Warning 'Chocolatey is unavailable.'
    }
    $probe = Get-Command ffprobe -CommandType Application -ErrorAction SilentlyContinue
    $encoder = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue
}

$available = $null -ne $probe -and $null -ne $encoder
if ($available) {
    foreach ($tool in @($probe, $encoder)) {
        try {
            & $tool.Source -version
            if ($LASTEXITCODE -ne 0) { $available = $false }
        } catch [System.Management.Automation.ApplicationFailedException] {
            Write-Warning "The media tool could not be started: $($tool.Source)"
            $available = $false
        }
    }
}

if ($available) {
    @("CONTENTS_TEST_FFPROBE=$($probe.Source)", "CONTENTS_TEST_FFMPEG=$($encoder.Source)", 'CONTENTS_TEST_MEDIA_UNAVAILABLE=0') |
        Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
    'available=true' | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
} else {
    @('CONTENTS_TEST_FFPROBE=', 'CONTENTS_TEST_FFMPEG=', 'CONTENTS_TEST_MEDIA_UNAVAILABLE=1') |
        Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
    'available=false' | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    Write-Output '::warning::FFmpeg/ffprobe could not be prepared. Dependent tests will be skipped; other tests will continue.'
    @('', '### Media tests', '', 'FFmpeg/ffprobe could not be prepared. Tests that require these tools are skipped, with reasons recorded in the test results.') |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}

# Chocolateyの終了コードを、前提不足を記録済みの準備ステップへ持ち越しません。
exit 0
