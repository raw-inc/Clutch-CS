#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $StateDirectory,
    [uri] $Listen = 'http://127.0.0.1:5081/',
    [switch] $NoBrowser
)
$ErrorActionPreference = 'Stop'
if (-not $Listen.IsLoopback -or $Listen.Scheme -ne 'http') { throw 'ClientHost must listen on a loopback HTTP URL.' }
if (-not [IO.Path]::IsPathRooted($ExecutablePath) -or -not [IO.File]::Exists($ExecutablePath)) { throw 'An installed ClientHost executable is required.' }
if (-not [IO.Path]::IsPathRooted($StateDirectory)) { throw 'StateDirectory must be absolute.' }
$executable = [IO.Path]::GetFullPath($ExecutablePath)
$state = [IO.Path]::GetFullPath($StateDirectory)
$mutex = New-Object Threading.Mutex($false, ('Local\Contents-Client-' + $Listen.Port))
$owned = $false
try {
    try { $owned = $mutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another launcher has not completed.' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Listen.Port -ErrorAction SilentlyContinue)
    if ($listeners.Count) {
        $ids = @($listeners.OwningProcess | Select-Object -Unique)
        if ($ids.Count -ne 1) { throw 'The Dashboard port has multiple owners.' }
        $existing = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ids[0])
        if ($existing.ExecutablePath -ne $executable -or -not $existing.CommandLine.Contains($state)) { throw 'The Dashboard port belongs to another application or profile.' }
    } else {
        [void][IO.Directory]::CreateDirectory($state)
        $arguments = '--listen "' + $Listen.AbsoluteUri + '" --state "' + $state + '"'
        $started = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($executable)) -WindowStyle Hidden -PassThru
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        do {
            if ($started.HasExited) { throw 'ClientHost exited before its local API became available.' }
            try { $null = Invoke-RestMethod ($Listen.AbsoluteUri.TrimEnd('/') + '/client/network') -TimeoutSec 2; break } catch {
                if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
                Start-Sleep -Milliseconds 200
            }
        } while ($true)
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
if (-not $NoBrowser) { Start-Process ($Listen.AbsoluteUri.TrimEnd('/') + '/dashboard') }
