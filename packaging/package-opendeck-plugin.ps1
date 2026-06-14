param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "output"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$projectFile = Join-Path $repoRoot "src\OpenDeck.Loupedeck.csproj"

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet_cli"
$resolvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$publishRoot = Join-Path $resolvedOutputRoot "publish"
$expandedRoot = Join-Path $resolvedOutputRoot "expanded"
$pluginPackageName = "io.github.brendangrant.opendeck.loupedeck.sdPlugin"
$pluginDir = Join-Path $expandedRoot $pluginPackageName
$pluginArchive = Join-Path $resolvedOutputRoot $pluginPackageName
$temporaryZip = Join-Path $resolvedOutputRoot "io.github.brendangrant.opendeck.loupedeck.zip"
$runtimes = @("win-x64", "linux-arm64")

if (Test-Path $resolvedOutputRoot) {
    Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force
}

New-Item -ItemType Directory -Force $resolvedOutputRoot | Out-Null
New-Item -ItemType Directory -Force $publishRoot | Out-Null
New-Item -ItemType Directory -Force $expandedRoot | Out-Null
New-Item -ItemType Directory -Force $pluginDir | Out-Null

foreach ($runtime in $runtimes) {
    $publishDir = Join-Path $publishRoot $runtime
    $pluginRuntimeDir = Join-Path $pluginDir $runtime

    dotnet publish $projectFile `
        -c $Configuration `
        -r $runtime `
        --self-contained true `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime '$runtime' with exit code $LASTEXITCODE. Make sure a .NET 10 SDK is installed and selected."
    }

    New-Item -ItemType Directory -Force $pluginRuntimeDir | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $pluginRuntimeDir -Recurse -Force
    if (Test-Path (Join-Path $pluginRuntimeDir "manifest.json")) {
        Remove-Item -LiteralPath (Join-Path $pluginRuntimeDir "manifest.json") -Force
    }
    if (Test-Path (Join-Path $pluginRuntimeDir "images")) {
        Remove-Item -LiteralPath (Join-Path $pluginRuntimeDir "images") -Recurse -Force
    }
}

Copy-Item -LiteralPath (Join-Path $repoRoot "src\manifest.json") -Destination (Join-Path $pluginDir "manifest.json") -Force
New-Item -ItemType Directory -Force (Join-Path $pluginDir "images") | Out-Null
Copy-Item -Path (Join-Path $repoRoot "src\images\*") -Destination (Join-Path $pluginDir "images") -Recurse -Force

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
