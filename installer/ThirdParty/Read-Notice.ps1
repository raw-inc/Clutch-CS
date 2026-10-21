#Requires -Version 7.0

function Read-ThirdPartyNotice([string] $Path) {
    if ([IO.Path]::GetExtension($Path) -ne '.rtf') { return [IO.File]::ReadAllText($Path) }
    $inputHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $sources = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json)
    $transcriptions = @($sources | Where-Object { $_.sourceSha256 -eq $inputHash })
    if ($transcriptions.Count -ne 1) { throw "Review the plain-text transcription for the RTF notice: $Path" }
    $source = $transcriptions[0]
    $textPath = Join-Path $PSScriptRoot ('licenses/' + $source.file)
    if ((Get-FileHash -LiteralPath $textPath -Algorithm SHA256).Hash -ne $source.sha256) {
        throw "The reviewed license transcription has changed: $($source.file)"
    }
    return [IO.File]::ReadAllText($textPath)
}
