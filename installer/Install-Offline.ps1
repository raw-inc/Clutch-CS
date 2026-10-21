#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PayloadRoot,
    [Parameter(Mandatory)][string] $InstallationRoot,
    [Parameter(Mandatory)][string] $ConfigurationPath,
    [Parameter(Mandatory)][string] $ServiceArtifactRoot,
    [ValidateSet('Windows', 'Linux', 'MacOS')][string] $Platform = 'Windows',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string] $ServiceName = 'ReArtWord.Contents.BackgroundService',
    [switch] $RegisterAndStart,
    [string] $VerifierExecutablePath,
    [ValidateRange(1,600000)][int] $ReadyTimeoutMilliseconds = 60000,
    [scriptblock] $ActionInvoker
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Install-ServiceActions.ps1')

function Resolve-Absolute([string] $Value) {
    if (-not [IO.Path]::IsPathFullyQualified($Value)) { throw 'An absolute path is required.' }
    $resolved = [IO.Path]::GetFullPath($Value).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'A volume root is not permitted.' }
    $ancestor = $resolved
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Symbolic links and reparse points are not permitted.' }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $resolved
}

function Is-Inside([string] $Root, [string] $Path) {
    return $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$payload = Resolve-Absolute $PayloadRoot
$installation = Resolve-Absolute $InstallationRoot
$configuration = Resolve-Absolute $ConfigurationPath
$artifacts = Resolve-Absolute $ServiceArtifactRoot
if ((Is-Inside $payload $installation) -or (Is-Inside $installation $payload) -or (Is-Inside $installation $configuration) -or (Is-Inside $installation $artifacts)) { throw 'Payload, installation and external configuration/artifacts must be separated.' }
if (Test-Path -LiteralPath $installation) { throw 'Initial installation requires a new destination; use Updater for an existing installation.' }
$manifestPath = Join-Path $payload 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.releaseId -or -not $manifest.entryPoint -or -not $manifest.files -or -not $manifest.platform) { throw 'The release manifest is incomplete.' }
$prefix = switch ($Platform) { 'Windows' { 'win-' } 'Linux' { 'linux-' } 'MacOS' { 'osx-' } }
if (-not $manifest.platform.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'The release platform does not match the selected OS.' }
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    $relative = [string]$file.path
    if (-not $relative -or $relative -match '[\\:<>"|?*\x00-\x1f]' -or $relative.StartsWith('/') -or ($relative.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or -not $_ -or $_.EndsWith('.') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)' }) -or -not $paths.Add($relative) -or $relative -eq 'release-manifest.json') { throw 'A manifest path is invalid or duplicated.' }
    $source = Resolve-Absolute (Join-Path $payload $relative)
    if (-not (Is-Inside $payload $source) -or -not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -ne $file.size -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw 'A payload file failed size/checksum verification.' }
}
if (-not $paths.Contains([string]$manifest.entryPoint)) { throw 'The entry point is not included in the manifest.' }
if ([IO.Path]::GetExtension([string]$manifest.entryPoint) -eq '.dll') { throw 'Installer payload must include a published apphost executable as entryPoint.' }
$preflight = $null
if ($RegisterAndStart) {
    if (-not $VerifierExecutablePath -and -not $ActionInvoker) { throw 'RegisterAndStart requires the external Updater verifier executable.' }
    if (-not (Test-Path -LiteralPath $configuration -PathType Leaf)) { throw 'The external TOML configuration must exist before registration.' }
    [void][IO.Directory]::CreateDirectory($artifacts)
    $preflight = Invoke-InstallAction 'verify' @{configurationPath=$configuration;manifestPath=$manifestPath;checkReady=$false;timeoutMilliseconds=$ReadyTimeoutMilliseconds} $ActionInvoker $VerifierExecutablePath $artifacts
    if (-not $preflight.success -or $preflight.stage -ne 'preflight') { throw 'Installation prerequisites were not confirmed.' }
}
[void](New-Item -ItemType Directory -Path $installation)
foreach ($file in $manifest.files) {
    $destination = Resolve-Absolute (Join-Path $installation $file.path)
    if (-not (Is-Inside $installation $destination)) { throw 'The destination escaped installation.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Copy-Item -LiteralPath (Join-Path $payload $file.path) -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Installed content checksum does not match.' }
    if ($file.executable -and -not $IsWindows) { [IO.File]::SetUnixFileMode($destination, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherExecute) }
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $installation 'release-manifest.json')
[void][IO.Directory]::CreateDirectory($artifacts)
$executable = Join-Path $installation $manifest.entryPoint
switch ($Platform) {
    'Windows' {
        $escapedExecutable = $executable.Replace("'", "''")
        $escapedInstallation = $installation.Replace("'", "''")
        $escapedConfig = $configuration.Replace("'", "''")
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Set-WindowsServiceRecovery.ps1') -Destination (Join-Path $artifacts 'Set-WindowsServiceRecovery.ps1') -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ServiceActions.ps1') -Destination (Join-Path $artifacts 'Install-ServiceActions.ps1') -Force
        $registration = "#Requires -Version 7.0`n# Run explicitly as administrator after reviewing the paths.`n[CmdletBinding()]param([string] `$VerifierExecutablePath, [scriptblock] `$SecurityInvoker, [scriptblock] `$RecoveryCommandInvoker)`n`$ErrorActionPreference = 'Stop'`n. (Join-Path `$PSScriptRoot 'Install-ServiceActions.ps1')`nInvoke-InstallAction 'secure' @{installationRoot='$escapedInstallation';configurationPath='$escapedConfig'} `$SecurityInvoker `$VerifierExecutablePath `$PSScriptRoot`nNew-Service -Name '$ServiceName' -DisplayName 'Clutch-CS ($ServiceName)' -BinaryPathName ('`"' + '$escapedExecutable' + '`" --config `"' + '$escapedConfig' + '`"') -StartupType Automatic`n& (Join-Path `$PSScriptRoot 'Set-WindowsServiceRecovery.ps1') -ServiceName '$ServiceName' -CommandInvoker `$RecoveryCommandInvoker`nStart-Service -Name '$ServiceName'`n"
        [IO.File]::WriteAllText((Join-Path $artifacts 'Register-Service.ps1'), $registration)
    }
    'Linux' {
        $unitExecutable = $executable.Replace('\', '\\').Replace('"', '\"').Replace('%', '%%')
        $unitConfig = $configuration.Replace('\', '\\').Replace('"', '\"').Replace('%', '%%')
        $unit = "[Unit]`nDescription=Clutch-CS`nAfter=network-online.target`nStartLimitIntervalSec=0`n`n[Service]`nType=notify`nExecStart=`"$unitExecutable`" --config `"$unitConfig`"`nRestart=on-failure`nRestartSec=10s`nTimeoutStopSec=60`n`n[Install]`nWantedBy=multi-user.target`n"
        [IO.File]::WriteAllText((Join-Path $artifacts "$ServiceName.service"), $unit)
    }
    'MacOS' {
        $xmlExecutable = [Security.SecurityElement]::Escape($executable)
        $xmlConfig = [Security.SecurityElement]::Escape($configuration)
        $plist = "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<!DOCTYPE plist PUBLIC `"-//Apple//DTD PLIST 1.0//EN`" `"http://www.apple.com/DTDs/PropertyList-1.0.dtd`">`n<plist version=`"1.0`"><dict><key>Label</key><string>$ServiceName</string><key>ProgramArguments</key><array><string>$xmlExecutable</string><string>--config</string><string>$xmlConfig</string></array><key>RunAtLoad</key><true/><key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict><key>ThrottleInterval</key><integer>10</integer></dict></plist>`n"
        [IO.File]::WriteAllText((Join-Path $artifacts "$ServiceName.plist"), $plist)
    }
}
if ($RegisterAndStart) {
    $stage = 'register'
    try {
        $service = @{platform=$Platform;name=$ServiceName;executable=$executable;configuration=$configuration;artifacts=$artifacts}
        if ($Platform -eq 'Windows') {
            $stage = 'permissions'
            Invoke-InstallAction 'secure' @{installationRoot=$installation;configurationPath=$configuration} $ActionInvoker $VerifierExecutablePath $artifacts | Out-Null
        }
        $stage = 'register'
        Invoke-InstallAction 'register' $service $ActionInvoker $VerifierExecutablePath $artifacts | Out-Null
        if ($Platform -eq 'Windows') {
            $stage = 'recovery'
            Invoke-InstallAction 'recovery' $service $ActionInvoker $VerifierExecutablePath $artifacts | Out-Null
        }
        $stage = 'start'
        Invoke-InstallAction 'start' $service $ActionInvoker $VerifierExecutablePath $artifacts | Out-Null
        $stage = 'ready'
        $request = @{configurationPath=$configuration;manifestPath=(Join-Path $installation 'release-manifest.json');checkReady=$true;timeoutMilliseconds=$ReadyTimeoutMilliseconds;configurationHash=$preflight.configurationHash;manifestHash=$preflight.manifestHash;startedAfter=$preflight.startedAfter}
        $ready = Invoke-InstallAction 'verify' $request $ActionInvoker $VerifierExecutablePath $artifacts
        if (-not $ready.success -or $ready.stage -ne 'ready' -or $ready.peerId -ne $preflight.peerId -or $ready.clusterId -ne $preflight.clusterId -or $ready.platform -ne $preflight.platform) { throw 'The installed Peer did not reach verified Ready.' }
        $ready | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'installation-result.json') -Encoding utf8
        Write-Output "Registered, started and verified Ready: $($ready.clusterId)/$($ready.peerId)."
    } catch {
        @{success=$false;stage=$stage;reason='install.incomplete';installation=$installation;serviceName=$ServiceName} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'installation-result.json') -Encoding utf8
        throw "Installation did not complete at stage $stage. Retained installed files and any registered service; no implicit retry or rollback."
    }
} else {
    Write-Output "Installed verified release $($manifest.releaseId). Service artifacts: $artifacts. No OS service was registered or started."
}
