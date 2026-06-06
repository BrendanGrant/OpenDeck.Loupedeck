param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "output"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$projectFile = Join-Path $repoRoot "src\OpenDeck.Loupedeck.csproj"

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet_cli"
$publishDir = Join-Path $repoRoot "output\publish\opendeck-loupedeck-win"
$resolvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
if (Test-Path $resolvedOutputRoot) {
    Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force $resolvedOutputRoot | Out-Null
$expandedRoot = Join-Path $resolvedOutputRoot "expanded"
$pluginPackageName = "io.github.brendangrant.opendeck.loupedeck.sdPlugin"
$pluginDir = Join-Path $expandedRoot $pluginPackageName
$pluginArchive = Join-Path $resolvedOutputRoot $pluginPackageName
$temporaryZip = Join-Path $resolvedOutputRoot "io.github.brendangrant.opendeck.loupedeck.zip"

dotnet publish $projectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Make sure a .NET 10 SDK is installed and selected."
}

if (Test-Path $pluginDir) {
    Remove-Item -LiteralPath $pluginDir -Recurse -Force
}

New-Item -ItemType Directory -Force $expandedRoot | Out-Null
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $pluginDir -Recurse -Force

if (Test-Path $pluginArchive) {
    Remove-Item -LiteralPath $pluginArchive -Force
}
if (Test-Path $temporaryZip) {
    Remove-Item -LiteralPath $temporaryZip -Force
}

Compress-Archive -Path $pluginDir -DestinationPath $temporaryZip -Force
Move-Item -LiteralPath $temporaryZip -Destination $pluginArchive

Write-Host "Packaged plugin:"
Write-Host "  $pluginDir"
Write-Host "Importable plugin archive:"
Write-Host "  $pluginArchive"
