# Solana Unity SDK Evaluation

## Overview
The Solana Unity SDK ships a complete runtime stack for interacting with the Solana blockchain from Unity. Its `Web3` singleton manages wallet lifecycles, routes RPC traffic, publishes events, and optionally keeps NFT state in sync, providing a single entry point for gameplay systems.【F:Assets/Solana/Runtime/codebase/Web3.cs†L15-L188】【F:Assets/Solana/Runtime/codebase/Web3.cs†L319-L432】 The SDK targets WebGL, Android/iOS, and desktop through a common wallet adapter abstraction, and includes prefabs plus editor tooling so projects can integrate quickly.

## Core runtime entry point (`Web3`)
* Maintains a singleton with configurable RPC cluster, optional custom endpoints, and RPC rate limits that are pushed into whichever wallet is active.【F:Assets/Solana/Runtime/codebase/Web3.cs†L25-L89】  
* Raises login/logout/balance/NFT events and exposes shorthand accessors (`Web3.Rpc`, `Web3.Wallet`, etc.) for consumers that need the current session.【F:Assets/Solana/Runtime/codebase/Web3.cs†L92-L160】  
* Provides login helpers for the in-game wallet, Web3Auth, Solana wallet adapter (including xNFT detection), and exposes button-friendly methods for UI wiring.【F:Assets/Solana/Runtime/codebase/Web3.cs†L201-L289】  
* Handles NFT synchronization by deduplicating token accounts, loading off-chain metadata, throttling WebGL requests, and notifying listeners as content streams in.【F:Assets/Solana/Runtime/codebase/Web3.cs†L337-L432】

## Wallet stack
### `WalletBase` foundation
`WalletBase` underpins every wallet implementation, supplying RPC client management, streaming subscriptions, SOL/SPL transfers, transaction signing, message signing, recent blockhash caching, and websocket reconnection with a Unity-specific rate limiter.【F:Assets/Solana/Runtime/codebase/WalletBase.cs†L20-L400】

### `InGameWallet`
Implements a self-custodied wallet that encrypts keystores with PBKDF2, supports mnemonic or secret-key import, persists credentials to `PlayerPrefs`, and signs transactions/messages entirely on-device.【F:Assets/Solana/Runtime/codebase/InGameWallet.cs†L16-L110】【F:Assets/Solana/Runtime/codebase/InGameWallet.cs†L112-L198】

### `Web3AuthWallet`
Wraps Web3Auth’s hosted login flow, configuring redirect URIs, theming, and federated login providers before emitting an Ed25519 keypair for the active session. Subsequent sign/serialize routines run locally, so gameplay code can treat it like any other `WalletBase`.【F:Assets/Solana/Runtime/codebase/Web3AuthWallet.cs†L59-L171】

### `SolanaWalletAdapter` and platform adapters
The high-level wallet adapter delegates to platform-specific wallets—Solana Mobile (Android), WebGL wallet adapter UI, or Phantom/Solflare deep links—based on build platform.【F:Assets/Solana/Runtime/codebase/SolanaWalletAdapter.cs†L11-L76】  
* **WebGL**: Presents a wallet selection UI, supports xNFT detection, marshals callbacks from JavaScript, and batches multi-transaction signing inside `SolanaWalletAdapterWebGL`.【F:Assets/Solana/Runtime/codebase/SolanaWalletAdapterWebGL/SolanaWalletAdapterWebGL.cs†L13-L200】【F:Assets/Solana/Runtime/codebase/SolanaWalletAdapterWebGL/SolanaWalletAdapterWebGL.cs†L360-L432】  
* **Deep links**: `PhantomDeepLink` and `SolflareDeepLink` exchange encrypted session keys, persist sessions across launches, and construct login/sign/disconnect URIs for their native wallets.【F:Assets/Solana/Runtime/codebase/DeepLinkWallets/PhantomDeepLink.cs†L22-L199】【F:Assets/Solana/Runtime/codebase/DeepLinkWallets/SolflareDeepLink.cs†L8-L69】  
* **Solana Mobile Stack**: `SolanaMobileWalletAdapter` negotiates local associations, reauthorizes long-lived sessions, and signs transactions/messages via the Android mobile wallet adapter APIs.【F:Assets/Solana/Runtime/codebase/SolanaMobileStack/SolanaMobileWalletAdapter.cs†L16-L188】

### `SessionWallet`
Provides delegated session key support: it persists session keystores, derives program-specific PDAs, builds initialize/revoke instructions, and validates session-token lifetimes before reusing them.【F:Assets/Solana/Runtime/codebase/SessionWallet.cs†L18-L200】

## RPC, streaming, and threading utilities
* `WalletBase` lazily spins up RPC and websocket clients, auto-derives wss endpoints when none are provided, and exposes a task that waits for websocket readiness.【F:Assets/Solana/Runtime/codebase/WalletBase.cs†L322-L392】  
* `UnityRateLimiter` adapts the SDK’s rate-limiter interface to Unity’s runtime so RPC clients respect per-second hit caps.【F:Assets/Solana/Runtime/codebase/UnityRateLimiter.cs†L10-L71】  
* `MainThreadDispatcher` schedules work back onto Unity’s main thread, letting async RPC callbacks safely mutate gameplay state or raise events.【F:Assets/Solana/Runtime/codebase/utility/MainThreadDispatcher.cs†L20-L130】

## Supporting utilities & content loading
* `FileDownloader` streams textures, GIFs, and JSON metadata while supplying custom converters for public keys and creator lists so NFT assets hydrate consistently across platforms.【F:Assets/Solana/Runtime/codebase/utility/FileDownloader.cs†L18-L200】
* The SDK ships reusable helpers such as `ArrayHelpers` and `Base58Encoding` (defined alongside the runtime code) that Toolbelt systems can import for byte/encoding chores instead of duplicating implementations.【F:Assets/Solana/Runtime/codebase/utility/ArrayHelpers.cs†L1-L63】【F:Assets/Solana/Runtime/codebase/utility/Base58Encoding.cs†L1-L120】

## NFT & Metaplex support
* `Solana.Unity.SDK.Nft.Nft` handles metadata hydration, texture downloads, local caching, and subsequent reloads so WebGL builds can avoid redundant HTTP calls.【F:Assets/Solana/Runtime/codebase/nft/Nft.cs†L15-L124】
* `CandyMachineCommands` contains ready-made builders for initializing candy machines, uploading config lines, and minting NFTs using Metaplex guard programs—complete with PDA derivations and rent calculations.【F:Assets/Solana/Runtime/codebase/Metaplex/CandyMachine/CandyMachineCommands.cs†L21-L200】
* The editor Bundlr uploader computes upload budgets, funds Bundlr addresses when required, and posts assets to Arweave so large metadata/image payloads stay compatible with Metaplex flows.【F:Assets/Solana/Editor/Solana/Metaplex/CandyMachineManager/Upload/Methods/Bundlr/BundlrUploader.cs†L12-L175】

## Editor tooling
The `CandyMachineController` editor workflow orchestrates deploy/update flows: it can mint collection NFTs, upload item metadata, initialize candy machines, and retry metadata lookups when RPC responses lag, all from Unity’s editor UI.【F:Assets/Solana/Editor/Solana/Metaplex/CandyMachineManager/CandyMachineController.cs†L96-L199】

## SDK-provided UI resources
Wallet adapter prefabs live under `Resources/SolanaUnitySDK`, exposing a ready-made screen and button prefab that wire into the WebGL wallet adapter flow (`WalletAdapterScreen` is bound to the SDK assembly for runtime scripting).【F:Assets/Resources/SolanaUnitySDK/WalletAdapterUI.prefab†L728-L728】【F:Assets/Resources/SolanaUnitySDK/WalletAdapterUI.prefab†L1283-L1283】 The main `Web3` helper automatically loads these prefabs when projects do not provide overrides.【F:Assets/Solana/Runtime/codebase/Web3.cs†L263-L273】
The package also includes an editor exporter that copies the canonical prefabs into the project’s Resources folder on script reload so builds can load them without manual steps.【F:Assets/Solana/Runtime/codebase/Exporters/PrefabsExporter.cs†L1-L46】

## Toolbelt integration boundary
To keep gameplay isolated from SDK internals, the Solana Toolbelt now builds as its own assembly that explicitly references the SDK’s runtime DLLs, Metaplex helpers, and cryptography dependencies.【F:Assets/Solana_Toolbelt/Solana.Toolbelt.asmdef†L1-L21】 The Token Toss game compiles into a separate assembly that depends only on `Solana.Toolbelt` (plus TextMeshPro), preventing direct calls into the SDK and eliminating redundant wallet/RPC implementations outside the toolbelt layer.【F:Assets/__Scenes/Token_Toss_Game/TokenToss.asmdef†L1-L15】 All toolbelt RPC requests flow through the SDK’s `Web3` facade so the project inherits its connection management and rate limiting without parallel infrastructure.【F:Assets/Solana_Toolbelt/Managers/Wallet_Manager/UnityWalletInfrastructure.cs†L26-L58】【F:Assets/Solana_Toolbelt/_Data/_Scripts/SolanaConfiguration.cs†L333-L352】【F:Assets/Solana_Toolbelt/_Data/_Scripts/SolanaConfiguration.cs†L480-L512】
`WalletManager` resolves the SDK through the `Web3Facade`, wiring session, transaction, and verification services so gameplay systems only ever touch Toolbelt abstractions while still relying on `WalletBase` and `Web3` underneath.【F:Assets/Solana_Toolbelt/Managers/Wallet_Manager/WalletManager.cs†L38-L265】

With the duplicate caches removed, blockhash reuse and transaction submission rely directly on `WalletBase.GetBlockHash` and `WalletBase.SignAndSendTransaction`, ensuring the toolbelt inherits the SDK’s cache invalidation logic and submit retries instead of maintaining parallel implementations.【F:Assets/Solana_Toolbelt/Managers/Wallet_Manager/Services/WalletTransactionService.cs†L16-L117】【F:Assets/Solana_Toolbelt/Services/Owner_Governed_Asset_Ledger_Service/OwnerGovernedAssetLedgerService.cs†L19-L211】【F:Assets/Solana_Toolbelt/Services/Solana_NFT_Mint_Service/SolanaNFTMintService.cs†L70-L219】

## Takeaways for the Solana Toolbelt
* Lean on `Web3` for wallet lifecycle, balance/NFT events, and centralized RPC access instead of reimplementing listeners or balance polling.  
* Use the SDK wallet adapters (`InGameWallet`, `Web3AuthWallet`, `SolanaWalletAdapter`, `SessionWallet`) to cover every supported platform without custom glue.  
* Reuse the Metaplex helpers (`Nft`, `CandyMachineCommands`) and editor tooling to mint, fetch, and manage NFT data while keeping gameplay code focused on toolbelt interfaces.  
* Maintain the enforced assembly boundary so only the toolbelt touches SDK types, ensuring Token Toss remains SDK-agnostic while benefiting from the battle-tested runtime.
