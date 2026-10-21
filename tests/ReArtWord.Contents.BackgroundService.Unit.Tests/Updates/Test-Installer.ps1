[CmdletBinding()]
param([Parameter(Mandatory)][string] $InstallerPath)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('contents-installer-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
try {
    foreach ($platform in @('Windows','Linux','MacOS')) {
        $payload = Join-Path $fixtureRoot "$platform-payload"
        [void][IO.Directory]::CreateDirectory($payload)
        $file = Join-Path $payload 'contents.exe'
        [IO.File]::WriteAllText($file, 'offline fixture')
        $rid = switch($platform) { 'Windows' { 'win-x64' } 'Linux' { 'linux-x64' } 'MacOS' { 'osx-arm64' } }
        $manifest = @{releaseId='fixture';platform=$rid;entryPoint='contents.exe';compatibleReleaseIds=@();files=@(@{path='contents.exe';size=(Get-Item $file).Length;sha256=(Get-FileHash $file -Algorithm SHA256).Hash;executable=$false})}
        $manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $payload 'release-manifest.json')
        $install = Join-Path $fixtureRoot "$platform-install"
        $artifacts = Join-Path $fixtureRoot "$platform-artifacts"
        & $InstallerPath -PayloadRoot $payload -InstallationRoot $install -ConfigurationPath (Join-Path $fixtureRoot 'external.toml') -ServiceArtifactRoot $artifacts -Platform $platform
        if ((Get-FileHash (Join-Path $install 'contents.exe')).Hash -ne (Get-FileHash $file).Hash) { throw 'Installed content differs.' }
        $artifactName = switch ($platform) { 'Windows' { 'Register-Service.ps1' } 'Linux' { 'ReArtWord.Contents.BackgroundService.service' } 'MacOS' { 'ReArtWord.Contents.BackgroundService.plist' } }
        $artifact = Get-Item -LiteralPath (Join-Path $artifacts $artifactName)
        if (-not $artifact) { throw 'Service registration artifact is missing.' }
        if ($platform -eq 'Windows') {
            $tokens = $null; $errors = $null
            [void][Management.Automation.Language.Parser]::ParseFile($artifact.FullName, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Generated service script is invalid PowerShell.' }
            & (Join-Path $PSScriptRoot 'Test-WindowsServiceRecovery.ps1') -RecoveryScriptPath (Join-Path $artifacts 'Set-WindowsServiceRecovery.ps1') -RegistrationScriptPath $artifact.FullName
        }
        if ($platform -eq 'Linux') {
            $unit = @{}
            $section = ''
            foreach ($line in Get-Content -LiteralPath $artifact.FullName) {
                if ($line -match '^\[(.+)\]$') { $section = $Matches[1] }
                elseif ($line -match '^([^=]+)=(.*)$') { $unit[$section + '.' + $Matches[1]] = $Matches[2] }
            }
            if ($unit['Unit.Description'] -ne 'Clutch-CS') { throw 'The systemd description must use the product name.' }
            if ($unit['Unit.StartLimitIntervalSec'] -ne '0' -or $unit['Service.Restart'] -ne 'on-failure' -or $unit['Service.RestartSec'] -ne '10s' -or $unit['Install.WantedBy'] -ne 'multi-user.target') { throw 'Generated systemd recovery/startup policy is invalid.' }
        }
        if ($platform -eq 'MacOS') {
            [xml]$parsed = Get-Content -LiteralPath $artifact.FullName -Raw
            if (-not $parsed.plist.dict) { throw 'Generated launchd XML is invalid.' }
            $startup = $parsed.SelectSingleNode('/plist/dict/key[.="RunAtLoad"]/following-sibling::*[1]')
            $keepAlive = $parsed.SelectSingleNode('/plist/dict/key[.="KeepAlive"]/following-sibling::*[1]')
            $successfulExit = $keepAlive.SelectSingleNode('key[.="SuccessfulExit"]/following-sibling::*[1]')
            $throttle = $parsed.SelectSingleNode('/plist/dict/key[.="ThrottleInterval"]/following-sibling::*[1]')
            if ($startup.Name -ne 'true' -or $keepAlive.Name -ne 'dict' -or $successfulExit.Name -ne 'false' -or $throttle.Name -ne 'integer' -or $throttle.InnerText -ne '10') { throw 'Generated launchd recovery/startup policy is invalid.' }
        }
        $configuration = Join-Path $fixtureRoot 'external.toml'
        [IO.File]::WriteAllText($configuration, 'fixture configuration verified by injected action')
        $steps = [Collections.Generic.List[string]]::new()
        $flow = {
            param($action,$data)
            $steps.Add($action)
            if ($action -eq 'verify') { return @{success=$true;stage=$(if ($data.checkReady) { 'ready' } else { 'preflight' });configurationHash='fixed';manifestHash='fixed';startedAfter=[DateTimeOffset]::UtcNow;peerId='p';clusterId='c';platform='fixture'} }
        }.GetNewClosure()
        $flowArtifacts = Join-Path $fixtureRoot "$platform-flow-artifacts"
        & $InstallerPath -PayloadRoot $payload -InstallationRoot (Join-Path $fixtureRoot "$platform-flow") -ConfigurationPath $configuration -ServiceArtifactRoot $flowArtifacts -Platform $platform -RegisterAndStart -ActionInvoker $flow
        $expectedSteps = if ($platform -eq 'Windows') { 'verify,secure,register,recovery,start,verify' } else { 'verify,register,start,verify' }
        if (($steps -join ',') -ne $expectedSteps) { throw 'Installation recovery/start order is invalid.' }
        $failures = @('register','start','ready')
        if ($platform -eq 'Windows') { $failures += @('recovery','secure') }
        foreach ($failure in $failures) {
            $steps.Clear()
            $failingFlow = {
                param($action,$data)
                $steps.Add($action)
                if ($action -eq $failure -or ($failure -eq 'ready' -and $action -eq 'verify' -and $data.checkReady)) { throw 'Injected action failure.' }
                if ($action -eq 'verify') { return @{success=$true;stage='preflight';configurationHash='fixed';manifestHash='fixed';startedAfter=[DateTimeOffset]::UtcNow;peerId='p';clusterId='c';platform='fixture'} }
            }.GetNewClosure()
            $failedArtifacts = Join-Path $fixtureRoot "$platform-$failure-artifacts"
            $rejected = $false
            try { & $InstallerPath -PayloadRoot $payload -InstallationRoot (Join-Path $fixtureRoot "$platform-$failure-install") -ConfigurationPath $configuration -ServiceArtifactRoot $failedArtifacts -Platform $platform -RegisterAndStart -ActionInvoker $failingFlow } catch { $rejected = $true }
            $failureResult = Get-Content -LiteralPath (Join-Path $failedArtifacts 'installation-result.json') -Raw | ConvertFrom-Json
            if (-not $rejected -or $failureResult.success -or $failureResult.stage -ne $(if ($failure -eq 'secure') { 'permissions' } else { $failure })) { throw 'Failed installation stage was not retained.' }
            if ($failure -in @('register','recovery','secure') -and $steps.Contains('start')) { throw 'Start continued after a failed prerequisite.' }
        }
        [IO.File]::AppendAllText($file, 'corruption')
        $badInstall = Join-Path $fixtureRoot "$platform-corrupt"
        $rejected = $false
        try { & $InstallerPath -PayloadRoot $payload -InstallationRoot $badInstall -ConfigurationPath (Join-Path $fixtureRoot 'external.toml') -ServiceArtifactRoot $artifacts -Platform $platform } catch { $rejected = $true }
        if (-not $rejected -or (Test-Path -LiteralPath $badInstall)) { throw 'Corrupt release was not rejected before installation.' }
    }
    $bundleScript = Join-Path ([IO.Path]::GetDirectoryName($InstallerPath)) 'New-ReleaseBundle.ps1'
    & $bundleScript -BundleRoot $fixtureRoot -RelativePayloadPaths @('Windows-payload','Linux-payload','MacOS-payload')
    $bundle = Get-Content -LiteralPath (Join-Path $fixtureRoot 'release-bundle.json') -Raw | ConvertFrom-Json
    if (@($bundle.variants).Count -ne 3) { throw 'RID bundle is incomplete.' }
    Write-Output 'PASS: three OS startup/recovery artifacts, action order/failure boundaries, installed checksum, script/XML parse, corrupt payload rejected before deployment.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('contents-installer-test-', [StringComparison]::Ordinal)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
