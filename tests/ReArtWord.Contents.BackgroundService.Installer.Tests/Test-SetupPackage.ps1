#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArchivePath)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This test requires the Windows setup package.' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('clutch-setup-tests-' + [Guid]::NewGuid().ToString('N'))
$extracted = Join-Path $fixture '展開先 & (確認)!%'
$renamed = Join-Path $fixture '名前を変更した配布物'
$unrelated = Join-Path $fixture 'unrelated-working-directory'
$startupTimeoutMilliseconds = 30000
$exitTimeoutMilliseconds = 5000

function Assert-SetupLaunch([string] $Directory) {
    $entry = Join-Path $Directory 'Clutch-CS-Setup.exe'
    $files = Join-Path $Directory 'files'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($entry)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.WorkingDirectory = $unrelated
    $process.StartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started -or -not $process.WaitForInputIdle($startupTimeoutMilliseconds)) { throw 'The setup window did not initialize.' }
        $process.Refresh()
        if ($process.HasExited) { throw "Setup exited during startup: $($process.ExitCode)." }
        if ($process.MainModule.FileName -ne $entry) { throw 'A different setup executable was started.' }
        $modules = @($process.Modules)
        foreach ($name in @('hostfxr.dll', 'coreclr.dll', 'libSkiaSharp.dll')) {
            $module = @($modules | Where-Object { $_.ModuleName -eq $name })
            if ($module.Count -ne 1 -or $module[0].FileName -ne (Join-Path $files $name)) {
                throw "Setup did not load its bundled $name from the extracted files directory."
            }
        }
    } finally {
        try {
            if ($started) {
                # .NETのWindows版HasExitedは、互換性維持のためOSの終了シグナルより先に終了コードを確認します。
                # https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L285-L319
                # 終了コードが取得済みでもファイルは解放前の場合があるため、配置を動かす前のWaitForExitを省略しません。
                # 詳細と見直し条件はRuntime/Processes/ProcessCompletion.csと同じです。通常終了・強制終了の猶予は維持します。
                $closing = $process.HasExited -or $process.CloseMainWindow()
                if (-not $closing -or -not $process.WaitForExit($exitTimeoutMilliseconds)) {
                    if (-not $process.HasExited) { $process.Kill() }
                    if (-not $process.WaitForExit($exitTimeoutMilliseconds)) { throw 'The setup test process did not stop.' }
                }
            }
        } finally {
            $process.Dispose()
        }
    }
}

try {
    [void][IO.Directory]::CreateDirectory($unrelated)
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ArchivePath), $extracted)
    $entries = @(Get-ChildItem -LiteralPath $extracted -Force | Select-Object -ExpandProperty Name)
    if ($entries.Count -ne 2 -or 'Clutch-CS-Setup.exe' -notin $entries -or 'files' -notin $entries) {
        throw 'The package root must contain only Clutch-CS-Setup.exe and files.'
    }
    foreach ($path in @('core/release-manifest.json', 'client/release-manifest.json', 'updater/release-manifest.json', 'cli/release-manifest.json', 'fleet-release.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'installer/Install-Offline.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $extracted ('files/' + $path)) -PathType Leaf)) { throw "A bundled file is missing: $path." }
    }
    foreach ($component in @{ core = 'Clutch-CS.CoreService'; client = 'Clutch-CS.ClientHost' }.GetEnumerator()) {
        $directory = Join-Path $extracted ('files/' + $component.Key)
        $manifest = Get-Content -LiteralPath (Join-Path $directory 'release-manifest.json') -Raw | ConvertFrom-Json
        $executable = $component.Value + '.exe'
        if ($manifest.entryPoint -cne $executable) { throw "Unexpected entry point: $($manifest.entryPoint)" }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $directory $executable))
        if ($info.FileDescription -cne $component.Value -or $info.ProductVersion -cne $manifest.expectedBuildVersion) {
            throw "The executable name or version does not match its manifest: $executable"
        }
    }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    . (Join-Path $repository 'eng/native-skia/NativeSkia.ps1')
    $null = Get-VerifiedSkiaLibrary (Join-Path $extracted 'files') 'win-x64'
    if (Test-Path -LiteralPath (Join-Path $extracted 'files/libSkiaSharp.pdb')) { throw 'The package contains mismatched upstream Skia debug symbols.' }
    $notices = Get-Content -LiteralPath (Join-Path $extracted 'files/core/THIRD-PARTY-NOTICES.txt') -Raw
    if (-not $notices.Contains('Acorn 8.18.0') -or -not $notices.Contains('Selected license: Apache-2.0') -or -not $notices.Contains('Zstandard: Selected license: BSD-3-Clause')) { throw 'The distributed Core license notices are incomplete.' }
    if ($notices -match '(?m)^# DNG SDK\r?$|^# piex\r?$') { throw 'Notices still include a codec excluded from the native build.' }
    # セットアップ画面の起動だけを確認し、インストールやネットワーク探索は開始しません。
    Assert-SetupLaunch $extracted
    $fixturePrefix = [IO.Path]::GetFullPath($fixture).TrimEnd('\') + '\'
    foreach ($path in @($extracted, $renamed)) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture path.' }
    }
    Move-Item -LiteralPath $extracted -Destination $renamed
    Assert-SetupLaunch $renamed
    Write-Output 'Setup package: root layout, ZIP relocation, renamed Unicode paths and bundled runtime/GUI libraries passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolved).StartsWith('clutch-setup-tests-')) { throw 'Unexpected fixture path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
