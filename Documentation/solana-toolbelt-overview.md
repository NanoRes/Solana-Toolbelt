# Solana Toolbelt vs. Solana Unity SDK

## Summary
The Solana Toolbelt layers a configurable runtime, OGAL-focused services, storage automation, and UI abstractions on top of the base Solana Unity SDK, turning the SDK’s primitives into ready-made gameplay infrastructure for Unity teams.

## RPC Abstraction
Toolbelt configures the Solana Unity SDK with the RPC settings defined in `SolanaConfiguration`. `ToolbeltRuntime` applies the ordered RPC URL list (including per-endpoint priority ordering), the primary WebSocket endpoint, and the configured RPC rate limit before gameplay scripts run. The actual RPC client behavior—connection handling, retries, websocket streaming, and request semantics—continues to be provided by the Solana Unity SDK’s `Web3`/wallet stack rather than a Toolbelt-owned intent layer.

## Unique additions beyond the SDK
- **Centralised runtime & configuration** – `ToolbeltRuntime` automatically discovers the project’s `SolanaConfiguration`, wires in the wallet manager, NFT access manager, UI bridge, storage, and pricing data, and applies ordered RPC endpoints and rate limits before gameplay scripts run.
- **Service provider pattern** – `SolanaConfiguration.InitializeToolbeltServices` registers wallet, inventory, metadata, pricing, storage, and access services so scenes consume Toolbelt interfaces instead of raw SDK types, keeping dependencies clean and swappable. The OGAL helper is created in `SolanaConfiguration.RebuildRuntimeServicesAsync` via `BuildOwnerGovernedAssetLedgerServiceAsync` and accessed through `SolanaConfiguration.ownerGovernedAssetLedgerService`, not through `IToolbeltServiceProvider`.
- **OGAL transaction helpers** – `OwnerGovernedAssetLedgerService` wraps Owner-Governed Asset Ledger mint/update/admin flows, deriving PDAs, validating collection authority, caching blockhashes, and translating Anchor errors into player-facing messages—capabilities not provided by the SDK alone.
- **Creator tooling for UGC** – `LevelEditorMintService` serialises level data, produces metadata plus OGAL-compatible mint requests, and leaves UI integration to the host via `IToolbeltUiBridge` or game-specific UI, while `SolanaNFTMintService` batches mint transactions with rent lookups, memo support, and retry logic to shield teams from low-level transaction assembly.
- **Storage & Bundlr automation** – The configuration asset can snapshot Bundlr balances, fund the uploader wallet, enqueue deposits, and top up automatically before `INftStorageUploader` and `ILevelJsonUploader`-driven mint flows upload JSON payloads—far beyond the SDK’s basic upload helpers.
- **Wallet lifecycle orchestration** – `WalletManager` and `WalletSessionService` manage login providers, editor testing keys, verification state, streaming health, and balance/memo events, exposing high-level tasks and Unity events to the rest of the project.
- **Access control & persistence** – `SolanaNftAccessManager` plugs into Toolbelt services to watch NFT ownership, cache unlock flags, poll RPC, and surface Unity events/UI prompts for token-gated features.
- **Metadata robustness** – `MetadataQueryService` fetches on-chain metadata accounts, loads off-chain JSON with IPFS gateway fallback, and validates content hashes so builds remain resilient when gateways degrade.
- **UI bridge contracts** – `IToolbeltUiBridge` defines the popup, progress, and mint dialogs Toolbelt flows expect, letting developers map blockchain flows into their own UI without modifying Toolbelt internals.

## OGAL Account Helpers
- **OGAL mint/update/admin helpers** – `OwnerGovernedAssetLedgerService` issues mint, manifest update, pause, authority update, and namespace migration transactions while wiring the required PDA derivations and runtime error handling from `Runtime/Toolbelt/Services/Owner_Governed_Asset_Ledger_Service/`.
- **Registry/config parsing** – `OwnerGovernedAssetLedgerConfigAccount` (and related OGAL models) deserialize registry state, including authority, bumps, namespace, and pause flags, so UI and gameplay layers can inspect configuration data without manual Borsh parsing.
- **Collection authority validation** – the service validates collection metadata and master edition authority before minting, guarding against mismatched update authority or non-unique master editions when verifying collections.
- **Creator verification** – mint requests sanitize creator lists and enforce verified creator signatures, downgrading creators when signatures are missing and surfacing actionable error messages.

## Why developers find it useful
- Converts the SDK’s low-level primitives into domain services that can be consumed via dependency injection, shrinking the amount of SDK-specific code gameplay teams must write or maintain.
- Prebuilt OGAL, minting, and Bundlr workflows deliver battle-tested transaction, storage, and error-handling logic, accelerating UGC, marketplace, and live-ops features without developers having to reverse-engineer on-chain programs.
- Wallet/session stack and UI bridge abstractions provide editor simulators, verification workflows, and popup hooks that teams can drop into scenes, giving consistent UX across WebGL, mobile, and desktop without rewriting adapters.

## Why Unity developers would pay for the Toolbelt
- Purchasing the Toolbelt buys a curated suite of production-ready systems—RPC failover, wallet orchestration, OGAL services, Bundlr automation, NFT access gating, and UI bridges—that would otherwise take significant engineering time to design, verify, and maintain on top of the SDK.
- The asset effectively packages expert knowledge of OGAL and Solana workflows into reusable components, reducing launch risk and enabling teams to focus on gameplay and UX rather than blockchain plumbing.
