# OpenDeck Loupedeck Plugin

Device plugin for using Loupedeck-family hardware with [OpenDeck](https://github.com/nekename/OpenDeck).

## What It Does

This plugin connects supported devices to OpenDeck and maps their hardware controls into the OpenDeck device model.

Current support includes:

- LCD key rendering
- LCD touch input
- encoder rotation
- encoder press/release
- device brightness control
- lower physical button forwarding on supported devices

## Supported Devices

- Loupedeck Live
- Razer Stream Controller
- Razer Stream Controller X

## Test Status

Implemented device profiles currently exist for the devices listed above.

Tested during development:

- Loupedeck Live
- Razer Stream Controller
- Razer Stream Controller X

Not yet tested:

- other Loupedeck-family devices
- other vendor-rebadged variants that present a different USB identity

## Current Limitations

- Supported runtime targets are currently Windows x64 and Linux ARM64
- Only the devices listed above are currently implemented
- The side LCD strips are not currently rendered by this plugin, so only the main 4x3 LCD grid is used for visual output
- The OpenDeck UI layout for encoders and extra buttons is constrained by OpenDeck's current device model
- Linux device identity and reconnect handling have been improved, but they still deserve more field testing

## Installation

1. Download a packaged `.sdPlugin` archive from Releases.
2. In OpenDeck, open `Plugins`.
3. Choose `Install from file`.
4. Select the plugin archive.
5. Restart OpenDeck if needed.

Make sure the official Loupedeck software is not running while this plugin is using the device.

On Linux, make sure OpenDeck already has whatever device access permissions it needs for your distribution.

## Building

Requirements:

- .NET 10 SDK
- Windows for local packaging with the provided PowerShell script

Build:

```powershell
dotnet build .\src\OpenDeck.Loupedeck.csproj
```

Package:

```powershell
.\packaging\package-opendeck-plugin.ps1
```

Output:

```text
output\io.github.brendangrant.opendeck.loupedeck.sdPlugin
```

The package contains:

- `win-x64/opendeck-loupedeck.exe`
- `linux-arm64/opendeck-loupedeck`

## Releases

This repository uses a tag-driven GitHub Actions release workflow.

To publish a release:

1. Update `src\manifest.json` and set the plugin `Version`.
2. Commit that change.
3. Create a matching Git tag in the form `v<version>`.
4. Push the commit and tag.

Example:

```powershell
git tag v0.1.0
git push origin main --tags
```

The release workflow validates that the Git tag version matches `src\manifest.json`, packages the plugin, and uploads the `.sdPlugin` archive to the GitHub Release.

## Credits

This plugin was built against the OpenDeck device-plugin model and the OpenAction API.

References:

- [OpenDeck](https://github.com/nekename/OpenDeck)
- [OpenDeck development guide](https://github.com/nekename/OpenDeck/blob/main/AGENTS.md)
- [OpenAction API](https://openaction.amankhanna.me/)
- [openaction device plugin docs](https://docs.rs/openaction/latest/openaction/device_plugin/index.html)
- [Stream Deck manifest reference](https://docs.elgato.com/streamdeck/sdk/references/manifest)

Additional ecosystem references:

- [4ndv/opendeck-akp03](https://github.com/4ndv/opendeck-akp03)
- [4ndv/opendeck-akp153](https://github.com/4ndv/opendeck-akp153)

## Project Files

- Manifest: `src\manifest.json`
- Project: `src\OpenDeck.Loupedeck.csproj`
- Packager: `packaging\package-opendeck-plugin.ps1`
