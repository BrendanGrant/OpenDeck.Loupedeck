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

## Current Limitations

- Windows only
- Only the devices listed above are currently implemented
- Requires OpenDeck device-plugin support
- The side LCD strips are not currently rendered by this plugin, so only the main 4x3 LCD grid is used for visual output
- The OpenDeck UI layout for encoders and extra buttons is constrained by OpenDeck's current device model
- Cross-platform packaging and device access are not implemented yet

## Installation

1. Download a packaged `.sdPlugin` archive from Releases.
2. In OpenDeck, open `Plugins`.
3. Choose `Install from file`.
4. Select the plugin archive.
5. Restart OpenDeck if needed.

Make sure the official Loupedeck software is not running while this plugin is using the device.

## Building

Requirements:

- Windows
- .NET 10 SDK

Build:

```powershell
dotnet build .\OpenDeck.Loupedeck\src\OpenDeck.Loupedeck.csproj
```

Package:

```powershell
.\OpenDeck.Loupedeck\packaging\package-opendeck-plugin.ps1
```

Output:

```text
output \io.github.brendangrant.opendeck.loupedeck.sdPlugin
```

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

- Manifest: `manifest.json`
- Project: `OpenDeck.Loupedeck.csproj`
- Packager: `package-opendeck-plugin.ps1`
