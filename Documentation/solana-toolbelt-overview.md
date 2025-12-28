# Solana Toolbelt vs. Solana Unity SDK

## Summary
The Solana Toolbelt layers a configurable runtime, OGAL-focused services, storage automation, and UI abstractions on top of the base Solana Unity SDK, turning the SDK’s primitives into ready-made gameplay infrastructure for Unity teams.

## RPC Abstraction
Toolbelt configures the Solana Unity SDK with the RPC settings defined in `SolanaConfiguration`. `ToolbeltRuntime` applies the ordered RPC URL list (including per-endpoint priority ordering), the primary WebSocket endpoint, and the configured RPC rate limit before gameplay scripts run. The actual RPC client behavior—connection handling, retries, websocket streaming, and request semantics—continues to be provided by the Solana Unity SDK’s `Web3`/wallet stack rather than a Toolbelt-owned intent layer.

## The Toolbelt deliberately does not
- Add implicit retry loops to every flow; most gameplay and service calls expect callers to surface errors and decide how to recover rather than silently reissuing requests.
- Replace the SDK’s transaction submission semantics or wallet client behavior; Toolbelt prefers to layer orchestration and UI prompts on top of the SDK instead of wrapping every RPC with hidden resilience logic.
- Guarantee “always-on” network success; it focuses on structured error handling and UX-friendly prompts, leaving retry policies explicit and configurable in targeted services.

## What the Toolbelt Does Not Guarantee
- Automatic retries for every RPC or transaction path. Most Toolbelt workflows avoid implicit retries to keep error handling deterministic and user-visible.
- However, some flows intentionally opt into retry behavior: OGAL minting supports configurable transport retries with optional secondary RPC failover plus creator-signature downgrade retries (see `OwnerGovernedAssetLedgerService` for `MintTransportRetryDecision` and the “Retrying with the creator downgraded” log line), and `RpcEndpointManager.ExecuteAsync` retries across endpoints for retryable errors.
- Guaranteed success on transient RPC failures; retry logic is scoped to specific services and relies on project configuration to enable or tune it.

## Unique additions beyond the SDK
- **Centralised runtime & configuration** – `ToolbeltRuntime` automatically discovers the project’s `SolanaConfiguration`, wires in the wallet manager, NFT access manager, UI bridge, storage, and pricing data, and applies ordered RPC endpoints and rate limits before gameplay scripts run.
- **Service provider pattern** – `SolanaConfiguration.InitializeToolbeltServices` registers wallet, inventory, metadata, pricing, storage, and access services so scenes consume Toolbelt interfaces instead of raw SDK types, keeping dependencies clean and swappable. OGAL is not registered via `IToolbeltServiceProvider`; it is built in `SolanaConfiguration.RebuildRuntimeServicesAsync` via `BuildOwnerGovernedAssetLedgerServiceAsync` and accessed through `SolanaConfiguration.ownerGovernedAssetLedgerService`.
- **OGAL transaction helpers** – `OwnerGovernedAssetLedgerService` wraps Owner-Governed Asset Ledger mint/update/admin flows, deriving PDAs, validating collection authority, caching blockhashes, and translating Anchor errors into player-facing messages—capabilities not provided by the SDK alone.
- **Creator tooling for UGC** – `LevelEditorMintService` serialises level data, produces metadata plus OGAL-compatible mint requests, and leaves UI integration to the host via `IToolbeltUiBridge` or game-specific UI, while `SolanaNFTMintService` batches mint transactions with rent lookups, memo support, and retry logic to shield teams from low-level transaction assembly.
- **Storage & Bundlr automation** – The configuration asset can snapshot Bundlr balances, fund the uploader wallet, enqueue deposits, and top up automatically before `INftStorageUploader` and `ILevelJsonUploader`-driven mint flows upload JSON payloads—far beyond the SDK’s basic upload helpers.
- **Wallet lifecycle orchestration** – `WalletManager` and `WalletSessionService` manage login providers, editor testing keys, verification state, streaming health, and balance/memo events, exposing high-level tasks and Unity events to the rest of the project.
- **Access control & persistence** – `SolanaNftAccessManager` plugs into Toolbelt services to watch NFT ownership, cache unlock flags, poll RPC, and surface Unity events/UI prompts for token-gated features.
- **Metadata robustness** – `MetadataQueryService` fetches on-chain metadata accounts, loads off-chain JSON with IPFS gateway fallback, and validates content hashes so builds remain resilient when gateways degrade.
- **UI bridge contracts** – `IToolbeltUiBridge` defines the popup, progress, and mint dialogs Toolbelt flows expect, letting developers map blockchain flows into their own UI without modifying Toolbelt internals.

## Uploader configuration and usage
Toolbelt ships two uploader families: the Bundlr/Irys uploader for Arweave-backed storage and HTTP uploaders for custom REST endpoints. Both flow into the same mint services via `ILevelJsonUploader` and `INftStorageUploader`.

### Bundlr/Irys uploader configuration
`BundlrUploader` (`Runtime/Toolbelt/Uploaders/Bundlr_Irys_Uploader/BundlrUploader.cs`) signs data items locally and posts them to the configured Bundlr/Irys node (`bundlrNodeUrl`). The resulting transaction IDs are turned into public URIs by appending them to the configured gateway (`arweaveGatewayUrl`, defaulting to `https://gateway.irys.xyz`).

Key configuration details:
- **Private key sources** – The uploader resolves the signing key in priority order: runtime override (`SetPrivateKey`), inspector `privateKeyBase58`, then the environment variable referenced by `privateKeyEnvironmentVariable`. The resolved key is cached into an `Account` for uploads.
- **Funding flow** – When Toolbelt needs to fund Bundlr, `SolanaConfiguration` first transfers SOL from the connected wallet to the uploader wallet, then `BundlrUploader.TryDepositAsync` submits a transfer from the uploader wallet to the Bundlr node’s Solana deposit address. `BundlrWalletKeyStore` can persist a generated private key per wallet for consistent uploads across sessions. `GetBundlrBalanceAsync` and `GetUploadPriceAsync` query the node for balance/price, while `GetUploaderWalletLamportsAsync` checks the uploader wallet’s SOL balance before sending.
- **`checkBalanceBeforeUpload`** – When enabled, `BundlrUploadTransport` queries the Bundlr balance and upload price before every upload and throws if the balance is insufficient. Disable this if you want uploads to proceed without the preflight balance check.
- **Gateway URL** – The `arweaveGatewayUrl` controls the public URI returned after upload. The gateway is combined with the transaction ID (e.g., `https://gateway.irys.xyz/<txId>`), so use this field to point to a preferred Arweave gateway.

### HTTP uploader profiles
`HttpUploaderProfile` (`Runtime/Toolbelt/Uploaders/HTTP_JSON_Uploader/HttpUploaderProfile.cs`) stores reusable HTTP upload settings:
- **Authentication headers** – The profile can emit a primary authentication header (`authenticationHeaderName` + `authenticationHeaderValue`, defaulting to `Authorization`) plus any additional headers listed in `additionalHeaders`. These are attached to every request built by `HttpUploaderUtility`.
- **Response parsing** – `responseUriJsonPath` uses Newtonsoft JSONPath syntax to extract the URI from a JSON response body. If left empty, the raw response body is treated as the URI.

Profiles are consumed by:
- `HttpJsonUploader` (implements `ILevelJsonUploader`) for JSON-only uploads.
- `HttpNftStorageUploader` (implements `INftStorageUploader`) for media + JSON, with optional separate profiles for media and metadata.

### Choosing the right uploader
- **Bundlr/Irys** – Use when you want Arweave-backed storage with automatic Bundlr funding and balance checks. `BundlrUploader` implements both `ILevelJsonUploader` and `INftStorageUploader`, so a single component can power level metadata uploads and NFT media/JSON uploads.
- **HTTP uploaders** – Use when you need to integrate with a custom storage API (pinning services, project-specific endpoints, or centralized storage). `HttpJsonUploader` feeds `LevelEditorMintService` through `ILevelJsonUploader`, and `HttpNftStorageUploader` feeds `UserGeneratedNftMintService` through `INftStorageUploader`.

## OGAL Account Helpers
- **OGAL mint/update/admin helpers** – `OwnerGovernedAssetLedgerService` issues mint, manifest update, pause, authority update, and namespace migration transactions while wiring the required PDA derivations and runtime error handling from `Runtime/Toolbelt/Services/Owner_Governed_Asset_Ledger_Service/`.
- **Registry/config parsing** – `OwnerGovernedAssetLedgerConfigAccount` (and related OGAL models) deserialize registry state, including authority, bumps, namespace, and pause flags, so UI and gameplay layers can inspect configuration data without manual Borsh parsing.
- **Collection authority validation** – the service validates collection metadata and master edition authority before minting, guarding against mismatched update authority or non-unique master editions when verifying collections.
- **Creator verification** – mint requests sanitize creator lists and enforce verified creator signatures, downgrading creators when signatures are missing and surfacing actionable error messages.

## Why developers find it useful
- Converts the SDK’s low-level primitives into domain services that can be consumed via dependency injection, shrinking the amount of SDK-specific code gameplay teams must write or maintain.
- Prebuilt OGAL, minting, and Bundlr workflows deliver battle-tested transaction, storage, and error-handling logic, accelerating UGC, marketplace, and live-ops features without developers having to reverse-engineer on-chain programs.
- Wallet/session stack and UI bridge abstractions provide editor simulators, verification workflows, and popup hooks that teams can drop into scenes, giving consistent UX across WebGL, mobile, and desktop without rewriting adapters.

## The Toolbelt deliberately does not
- **Replace the SDK’s RPC stack/protocols** – the Toolbelt still relies on the Solana Unity SDK’s RPC clients and request semantics, but it does provide endpoint selection plus retry/failover orchestration via `RpcEndpointManager` (`Runtime/Toolbelt/_Data/_Scripts/DomainServices/RpcEndpointManager.cs`).

## Why Unity developers would pay for the Toolbelt
- Purchasing the Toolbelt buys a curated suite of production-ready systems—RPC failover, wallet orchestration, OGAL services, Bundlr automation, NFT access gating, and UI bridges—that would otherwise take significant engineering time to design, verify, and maintain on top of the SDK.
- The asset effectively packages expert knowledge of OGAL and Solana workflows into reusable components, reducing launch risk and enabling teams to focus on gameplay and UX rather than blockchain plumbing.
