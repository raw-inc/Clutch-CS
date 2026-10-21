# Dot-sourced implementation boundary. Tests inject actions instead of touching the OS service manager.
function Invoke-InstallAction([string] $Action, [hashtable] $Data, [scriptblock] $Invoker, [string] $Verifier, [string] $Artifacts) {
    if ($Invoker) { return & $Invoker $Action $Data }
    if ($Action -eq 'secure') {
        if (-not $IsWindows) { throw 'Windows service permissions require Windows.' }
        if (-not [IO.Path]::IsPathFullyQualified($Verifier) -or -not (Test-Path -LiteralPath $Verifier -PathType Leaf)) { throw 'Protecting service files requires the external Updater executable.' }
        $requestPath = Join-Path $Artifacts ('secure-' + [Guid]::NewGuid().ToString('N') + '.json')
        $Data | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8
        & $Verifier --secure-install $requestPath
        if ($LASTEXITCODE -ne 0) { throw 'Service file permissions or payload verification failed. No service was registered.' }
        return
    }
    if ($Action -eq 'verify') {
        if (-not [IO.Path]::IsPathFullyQualified($Verifier) -or -not (Test-Path -LiteralPath $Verifier -PathType Leaf)) { throw 'The Updater verifier must be an existing absolute executable path.' }
        $requestPath = Join-Path $Artifacts ('verify-' + [Guid]::NewGuid().ToString('N') + '.json')
        $Data | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $requestPath -Encoding utf8
        $output = & $Verifier --verify-install $requestPath
        $verifierExitCode = $LASTEXITCODE
        $reportPath = [IO.Path]::ChangeExtension($requestPath, 'result.json')
        $output | Set-Content -LiteralPath $reportPath -Encoding utf8
        $result = $output | ConvertFrom-Json
        if ($verifierExitCode -ne 0) { throw "The read-only Updater verifier rejected installation at stage '$($result.stage)'. See $reportPath." }
        return $result
    }
    if ($Action -notin @('register','recovery','start')) { throw 'Unknown installation action.' }
    if ($Action -eq 'recovery') {
        if ($Data.platform -ne 'Windows') { throw 'The recovery action is only valid for Windows services.' }
        & (Join-Path $PSScriptRoot 'Set-WindowsServiceRecovery.ps1') -ServiceName $Data.name
        return
    }
    switch ($Data.platform) {
        'Windows' {
            if (-not $IsWindows) { throw 'Windows service registration requires Windows PowerShell 7.' }
            if ($Action -eq 'register') {
                if (Get-Service -Name $Data.name -ErrorAction SilentlyContinue) { throw 'The service name is already registered.' }
                if ($Data.executable.Contains('"') -or $Data.configuration.Contains('"')) { throw 'Service paths cannot contain a quote.' }
                $binary = '"' + $Data.executable + '" --config "' + $Data.configuration + '"'
                New-Service -Name $Data.name -DisplayName "Clutch-CS ($($Data.name))" -BinaryPathName $binary -StartupType Automatic | Out-Null
            } else { Start-Service -Name $Data.name }
        }
        'Linux' {
            if (-not $IsLinux) { throw 'systemd registration requires Linux.' }
            $unit = '/etc/systemd/system/' + $Data.name + '.service'
            if ($Action -eq 'register') {
                if (Test-Path -LiteralPath $unit) { throw 'The systemd unit already exists.' }
                Copy-Item -LiteralPath (Join-Path $Data.artifacts ($Data.name + '.service')) -Destination $unit
                [IO.File]::SetUnixFileMode($unit, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherRead)
                & /usr/bin/systemctl daemon-reload
                if ($LASTEXITCODE -ne 0) { throw 'systemd daemon-reload failed.' }
                & /usr/bin/systemctl enable ($Data.name + '.service')
            } else { & /usr/bin/systemctl start ($Data.name + '.service') }
            if ($LASTEXITCODE -ne 0) { throw 'systemd registration/start failed.' }
        }
        'MacOS' {
            if (-not $IsMacOS) { throw 'launchd registration requires macOS.' }
            $plist = '/Library/LaunchDaemons/' + $Data.name + '.plist'
            if ($Action -eq 'register') {
                if (Test-Path -LiteralPath $plist) { throw 'The launchd service already exists.' }
                Copy-Item -LiteralPath (Join-Path $Data.artifacts ($Data.name + '.plist')) -Destination $plist
                [IO.File]::SetUnixFileMode($plist, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherRead)
            } else {
                & /bin/launchctl bootstrap system $plist
                if ($LASTEXITCODE -ne 0) { throw 'launchd bootstrap failed.' }
            }
        }
        default { throw 'Unknown installation platform.' }
    }
}
