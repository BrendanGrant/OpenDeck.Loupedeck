param(
    [string]$PackagePath = "..\output\io.github.brendangrant.opendeck.loupedeck.sdPlugin",
    [string]$PluginsRoot = "$env:APPDATA\opendeck\plugins",
    [string]$PluginId = "io.github.brendangrant.opendeck.loupedeck.sdPlugin",
    [bool]$RestartOpenDeck = $true,
    [string]$OpenDeckExe = "$env:LOCALAPPDATA\OpenDeck\opendeck.exe"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedPackagePath = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot $PackagePath))
$resolvedPluginsRoot = [System.IO.Path]::GetFullPath($PluginsRoot)
$installedPluginDir = Join-Path $resolvedPluginsRoot $PluginId

if (-not (Test-Path -LiteralPath $resolvedPackagePath)) {
    throw "Plugin package not found: $resolvedPackagePath"
}

New-Item -ItemType Directory -Force $resolvedPluginsRoot | Out-Null

if ($RestartOpenDeck) {
    Get-Process -Name "opendeck" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process -Name "opendeck-loupedeck" -ErrorAction SilentlyContinue | Stop-Process -Force
}

if (Test-Path -LiteralPath $installedPluginDir) {
    Remove-Item -LiteralPath $installedPluginDir -Recurse -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($resolvedPackagePath, $resolvedPluginsRoot, $true)

Write-Host "Installed plugin:"
Write-Host "  $installedPluginDir"

if ($RestartOpenDeck) {
    if (Test-Path -LiteralPath $OpenDeckExe) {
        Start-Process -FilePath $OpenDeckExe -WindowStyle Hidden
        Write-Host "Restarted OpenDeck:"
        Write-Host "  $OpenDeckExe"
    }
    else {
        Write-Warning "OpenDeck executable was not found at '$OpenDeckExe'. Install succeeded, but OpenDeck was not restarted."
    }
}
