# NanoRes Solana Toolbelt

The **NanoRes Solana Toolbelt** is a Unity package that bundles runtime libraries, prefabs, wallet adapters, and OGAL services for building Solana-enabled user generated content experiences. It wraps the Solana Unity SDK, provides ready-made integrations for the Owner Governed Asset Ledger (OGAL), and includes utilities to simplify connecting wallets, minting NFTs, and interacting with on-chain programs from C#.

## Package Layout

```
Runtime/
  Android/                # Android templates and native plugins
  Packages/               # Precompiled Solana SDK libraries
  Plugins/                # JavaScript, native, and managed plugin sources
  Resources/              # Shared icons, prefabs, and UI assets
  Toolbelt/               # OGAL managers, services, and utility scripts
  codebase/               # Core Solana Unity SDK sources
Editor/
  Solana/                 # Editor tooling, setup wizard, and inspectors
Samples~/
  Solana Wallet/          # Complete wallet connection sample
Documentation~/           # Extended documentation and evaluation notes
package.json              # Unity package manifest
```

All folders are organised according to the Unity Package Manager (UPM) conventions, which allows the repository to be consumed directly from Git or uploaded to the Unity Asset Store.

## Installation

### From Git (Unity Package Manager)

1. Open **Unity** and navigate to **Window → Package Manager**.
2. Choose **Add package from git URL…**.
3. Enter the repository URL, for example:
   ```
   https://github.com/NanoRes-Studios/Solana-Toolbelt.git
   ```
4. Unity will download the package and import the runtime, editor, and sample content.

> **Dependencies**: All dependencies are resolved automatically, including [UniTask](https://github.com/Cysharp/UniTask) which is pulled directly from its official Git repository. No additional scoped registries (e.g., OpenUPM) are required.

### As a Unity Asset Store Package

Once published by NanoRes Studios, locate **NanoRes Solana Toolbelt** on the Unity Asset Store and press **Add to My Assets**. Use the Unity Package Manager to download it into your project.

## Samples

The package ships with a **Solana Wallet Demo** sample that showcases wallet connection flows, UI prefabs, and transaction helpers. Install it from **Window → Package Manager → NanoRes Solana Toolbelt → Samples**.

## Documentation

Extended guides, SDK evaluation notes, and QA findings are located under `Documentation~/`. When the package is added to a project those files appear in the Unity Package Manager documentation links.

## Requirements

- Unity **2021.3 LTS** or newer (tested with 2021.3+)
- [TextMesh Pro](https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.0/manual/index.html) and the [Newtonsoft Json package](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html) — both declared as dependencies and installed automatically.

## Support

The package is maintained by **NanoRes Studios**. Please open an issue in the repository or reach out through the Asset Store support channels for feature requests and bug reports.
