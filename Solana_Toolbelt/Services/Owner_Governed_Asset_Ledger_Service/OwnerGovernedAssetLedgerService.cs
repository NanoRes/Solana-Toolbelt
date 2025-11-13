using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Net.Http;
using System.Net.Sockets;
using System.Globalization;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Programs;
using Solana.Unity.Wallet;
using Solana.Unity.SDK;
using UnityEngine;
using Solana.Unity.Rpc.Messages;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Client-side helper that wraps calls to the custom OwnerGovernedAssetLedger
    /// program.  The service constructs and sends transactions to mint object
    /// NFTs, update object manifests and fetch existing manifest data.
    /// </summary>
    public class OwnerGovernedAssetLedgerService : IOGALNftService
    {
        private readonly IRpcClient _rpcClient;
        private readonly IRpcClient _secondaryRpcClient;
        private readonly Func<WalletBase> _walletResolver;
        private readonly PublicKey _programId;
        private readonly PublicKey _collectionMint;
        private readonly PublicKey _configNamespace;
        private readonly PublicKey _registryConfigOverride;
        private readonly PublicKey _mintAuthorityOverride;
        private readonly uint _mintComputeUnitLimit;
        private readonly ulong? _mintComputeUnitPriceMicroLamports;
        private readonly int _blockhashMaxSeconds;
        private readonly object _blockhashCacheLock = new();
        private readonly Dictionary<IRpcClient, BlockhashCacheEntry> _cachedBlockhashes = new();
        private readonly int _mintTransportRetryCount;
        private readonly int _mintTransportRetryDelayMilliseconds;

        private const string CollectionAuthorityGuardRailErrorSubstring = "custom program error: 0x65";
        private const string CollectionMasterEditionGuardRailErrorSubstring = "custom program error: 0x52";
        private const string CreatorVerificationErrorSubstring = "custom program error: 0x36";
        private const uint DefaultMintComputeUnitLimit = 400_000;

        private const string CollectionMasterEditionNotUniqueMessage =
            "The collection master edition must have max supply 0. Ask the studio team to convert the collection master edition to a unique edition and try again.";

        private static readonly IReadOnlyDictionary<string, string> AnchorErrorFriendlyMessages =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["InvalidAuthority"] = "Connect with the registry authority wallet to mint objects from this registry.",
                ["MintingPaused"] = "Minting is currently paused. Please try again later.",
                ["UriTooLong"] = "The uploaded metadata URI is too long for the registry. Shorten the URI and try again.",
                ["ObjectInactive"] = "This object is inactive and cannot be minted right now.",
                ["ManifestMismatch"] = "The uploaded object data does not match the on-chain manifest. Please refresh and try again.",
                ["InvalidOwnerTokenAccount"] = "You must use the wallet that owns this object NFT to submit an update.",
                ["OwnerDoesNotHoldObjectNft"] = "The connected wallet no longer holds this object NFT. Acquire it before updating.",
                [CollectionAuthorityGuardRailErrorSubstring] = "The collection NFT rejected the verification step. Ask the studio team to update the collection's update authority to the registry mint authority before minting new levels.",
                [CollectionMasterEditionGuardRailErrorSubstring] = CollectionMasterEditionNotUniqueMessage,
                [CreatorVerificationErrorSubstring] = "The mint request marks a creator as verified but that wallet did not sign the transaction. Either remove the verified flag or have the creator approve the mint."
            };

        private const string UnknownMintErrorMessage = "Minting the object failed due to an unexpected error.";
        private const string UnknownUpdateErrorMessage = "Updating the object failed due to an unexpected error.";
        private const string UnknownAuthorityUpdateErrorMessage =
            "Updating the registry authority failed due to an unexpected error.";
        private const string UnknownPauseUpdateErrorMessage =
            "Updating the registry pause status failed due to an unexpected error.";
        private const string UnknownMigrationErrorMessage =
            "Migrating the registry namespace failed due to an unexpected error.";
        private const string CollectionAuthorityMismatchMessage =
            "The collection NFT is controlled by an unexpected update authority. Ask the studio team to update the collection before minting new levels.";
        private const string CollectionMetadataUnavailableMessage =
            "Unable to fetch the collection NFT metadata from the RPC endpoint. Please try again.";
        private const string CollectionMasterEditionUnavailableMessage =
            "Unable to fetch the collection NFT master edition from the RPC endpoint. Please try again.";
        private const int PublicKeyLength = 32;
        private const int MetadataUpdateAuthorityOffset = 1;
        private const byte MasterEditionV1Discriminator = 2;
        private const byte MasterEditionV2Discriminator = 6;

        private static readonly char[] HexAlphabet = "0123456789ABCDEF".ToCharArray();

        private static readonly byte[] MintObjectDiscriminator = CreateAnchorDiscriminator("global:mint_object_nft");
        private static readonly byte[] UpdateManifestDiscriminator = CreateAnchorDiscriminator("global:update_object_manifest");
        private static readonly byte[] SetAuthorityDiscriminator = CreateAnchorDiscriminator("global:set_authority");
        private static readonly byte[] SetPausedDiscriminator = CreateAnchorDiscriminator("global:set_paused");
        private static readonly byte[] MigrateConfigNamespaceDiscriminator =
            CreateAnchorDiscriminator("global:migrate_config_namespace");
        private static readonly byte[] ConfigAccountDiscriminator = CreateAnchorDiscriminator("account:Config");
        private static readonly byte[] ManifestAccountDiscriminator = CreateAnchorDiscriminator("account:ObjectManifest");

        private static readonly byte[] RegistryConfigSeed = Encoding.UTF8.GetBytes("config");
        private static readonly byte[] MintAuthoritySeed = Encoding.UTF8.GetBytes("auth");
        private static readonly byte[] ManifestSeed = Encoding.UTF8.GetBytes("object_manifest");
        private static readonly byte[] MintSeed = Encoding.UTF8.GetBytes("object_mint");
        private static readonly PublicKey TokenMetadataProgramId = new("metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s");
        private static readonly PublicKey RentSysvarId = new("SysvarRent111111111111111111111111111111111");
        private static readonly PublicKey InstructionsSysvarId =
            new("Sysvar1nstructions1111111111111111111111111");
        public OwnerGovernedAssetLedgerService(
            IRpcClient rpcClient,
            Func<WalletBase> walletResolver,
            string programId,
            string collectionMint,
            string configNamespace,
            string registryConfigAccount = null,
            string mintAuthorityAccount = null,
            int blockhashMaxSeconds = 0,
            uint mintComputeUnitLimit = DefaultMintComputeUnitLimit,
            ulong? mintComputeUnitPriceMicroLamports = null)
            : this(
                rpcClient,
                walletResolver,
                programId,
                collectionMint,
                configNamespace,
                registryConfigAccount,
                mintAuthorityAccount,
                blockhashMaxSeconds,
                mintComputeUnitLimit,
                mintComputeUnitPriceMicroLamports,
                null,
                0,
                0)
        {
        }

        public OwnerGovernedAssetLedgerService(
            IRpcClient rpcClient,
            Func<WalletBase> walletResolver,
            string programId,
            string collectionMint,
            string configNamespace,
            string registryConfigAccount,
            string mintAuthorityAccount,
            int blockhashMaxSeconds,
            uint mintComputeUnitLimit,
            ulong? mintComputeUnitPriceMicroLamports,
            IRpcClient secondaryRpcClient,
            int mintTransportRetryCount,
            int mintTransportRetryDelayMilliseconds)
        {
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _secondaryRpcClient = secondaryRpcClient;
            _walletResolver = walletResolver ?? throw new ArgumentNullException(nameof(walletResolver));
            _programId = new PublicKey(programId);
            _collectionMint = new PublicKey(collectionMint);
            _blockhashMaxSeconds = Math.Max(0, blockhashMaxSeconds);
            _mintComputeUnitLimit = mintComputeUnitLimit > 0 ? mintComputeUnitLimit : DefaultMintComputeUnitLimit;
            _mintComputeUnitPriceMicroLamports =
                mintComputeUnitPriceMicroLamports.HasValue && mintComputeUnitPriceMicroLamports.Value > 0
                    ? mintComputeUnitPriceMicroLamports
                    : null;
            _mintTransportRetryCount = Math.Max(0, mintTransportRetryCount);
            _mintTransportRetryDelayMilliseconds = Math.Max(0, mintTransportRetryDelayMilliseconds);
            if (!string.IsNullOrWhiteSpace(configNamespace))
            {
                _configNamespace = new PublicKey(configNamespace);
            }

            if (!string.IsNullOrWhiteSpace(registryConfigAccount))
            {
                _registryConfigOverride = new PublicKey(registryConfigAccount);
            }

            if (!string.IsNullOrWhiteSpace(mintAuthorityAccount))
            {
                _mintAuthorityOverride = new PublicKey(mintAuthorityAccount);
            }

            if (_registryConfigOverride == null && _configNamespace == null)
                throw new ArgumentException("Either a configuration namespace or registry config account is required.");
        }

        public async Task<OwnerGovernedAssetLedgerMintResult> MintObjectNftAsync(OwnerGovernedAssetLedgerMintRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] MintObjectNftAsync called. objectId = {request.ObjectId}, recipient = '{request.RecipientPublicKey}', " +
                $"manifestUri = '{request.ManifestUri}', metadataName = '{request.MetadataName}', creators = {request.Creators?.Count() ?? 0}," +
                $"mintBump = {FormatNullableByte(request.MintBump)}, ConfigNamespace = {(!string.IsNullOrWhiteSpace(request.ConfigNamespace) ? request.ConfigNamespace : "<null>")}, " +
                $"authBump = {FormatNullableByte(request.AuthBump)}, manifestBump = {FormatNullableByte(request.ManifestBump)}, " +
                $"manifestHash = {FormatByteArray(request.ManifestHash)}, configBump = {FormatNullableByte(request.ConfigBump)}, sellerFees = {request.SellerFeeBasisPoints}");

            var wallet = _walletResolver() ?? throw new InvalidOperationException("Wallet not connected");
            var payerAccount = wallet.Account ?? throw new InvalidOperationException("Wallet account unavailable");

            var permittedSignerPublicKeys = ResolveMintSignerPublicKeys(wallet, payerAccount.PublicKey);
            var permittedSignerLog = string.Join(
                ", ",
                permittedSignerPublicKeys
                    .Where(k => k != null && !string.IsNullOrWhiteSpace(k.Key))
                    .Select(k => k.Key));

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Validating creator signers for payer '{payerAccount.PublicKey}'. " +
                $"permittedSigners='{permittedSignerLog}'.");
            var sanitizedCreators = SanitizeMintCreators(
                request.Creators,
                payerAccount.PublicKey,
                permittedSignerPublicKeys);

            var recipientPublicKey = new PublicKey(request.RecipientPublicKey);
            ulong objectId = request.ObjectId;

            var configAccount = await FetchRegistryConfigAsync(
                request.ConfigNamespace,
                request.ConfigBump).ConfigureAwait(false);

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Registry config loaded. address='{configAccount.Address}', " +
                $"authority='{configAccount.Authority}', configBump={configAccount.ConfigBump}, authBump={configAccount.AuthBump}, " +
                $"objectCount={configAccount.ObjectCount}, namespace='{configAccount.Namespace}', paused={(configAccount.Paused ? "true" : "false")}.");

            var registryConfigPda = configAccount.Address;
            byte? authBump = request.AuthBump ?? configAccount.AuthBump;
            var derivedMintAuthority = DeriveMintAuthorityPda(registryConfigPda, authBump);
            var mintAuthorityPda = ResolveMintAuthorityAccount(registryConfigPda, authBump);
            bool mintAuthorityRequiresSignature =
                _mintAuthorityOverride != null && !_mintAuthorityOverride.Equals(derivedMintAuthority);
            var manifestPda = DeriveManifestPda(registryConfigPda, objectId, request.ManifestBump);
            var mintPubKey = DeriveObjectMintPda(manifestPda, request.MintBump);
            var recipientAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(recipientPublicKey, mintPubKey);
            var metadataPda = DeriveMetadataPda(mintPubKey);
            var masterEditionPda = DeriveMasterEditionPda(mintPubKey);
            var collectionMetadataPda = DeriveMetadataPda(_collectionMint);
            var collectionMasterEditionPda = DeriveMasterEditionPda(_collectionMint);

            await ValidateCollectionUpdateAuthorityAsync(
                    collectionMetadataPda,
                    collectionMasterEditionPda,
                    mintAuthorityPda)
                .ConfigureAwait(false);

            Debug.Log("[OwnerGovernedAssetLedgerService] Preparing mint transaction with derived accounts.");
            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Constructed mint PDAs. manifestPda='{manifestPda}', mintPubKey='{mintPubKey}', recipientAta='{recipientAta}'.");

            bool retriedWithDowngradedCreators = false;
            List<string> downgradedCreatorAddresses = null;
            string serializedTransaction = null;
            RequestResult<string> sendRes = null;
            string firstSerializedTransaction = null;
            string lastSerializedTransaction = null;
            bool forcedCreatorDowngradeAfterProgramError = false;
            Transaction signedTransaction = null;
            IRpcClient currentRpcClient = _rpcClient;
            bool usingSecondaryRpc = false;
            int transportRetryAttempts = 0;
            bool forceBlockhashRefresh = false;

            async Task<bool> HandleTransportFailureAsync(string errorMessage, Exception exception)
            {
                bool isTransportError = exception != null ? IsTransportError(exception) : IsTransportErrorMessage(errorMessage);
                if (!isTransportError)
                {
                    return false;
                }

                transportRetryAttempts++;
                var decision = DetermineTransportRetryDecision(
                    transportRetryAttempts,
                    _mintTransportRetryCount,
                    _secondaryRpcClient != null,
                    usingSecondaryRpc);

                switch (decision)
                {
                    case MintTransportRetryDecision.RetryPrimary:
                        var retryDelay = CalculateExponentialBackoffDelay(
                            transportRetryAttempts,
                            _mintTransportRetryDelayMilliseconds);
                        Debug.LogWarning(
                            $"[OwnerGovernedAssetLedgerService] Transport error while sending mint transaction. " +
                            $"attempt={transportRetryAttempts}, maxAttempts={_mintTransportRetryCount}, usingSecondary={usingSecondaryRpc}, " +
                            $"delayMs={retryDelay}, exceptionType='{exception?.GetType().Name ?? "<null>"}', message='{errorMessage}'. " +
                            "Retrying on primary RPC endpoint with a refreshed blockhash.");
                        if (retryDelay > 0)
                        {
                            await Task.Delay(retryDelay).ConfigureAwait(false);
                        }

                        forceBlockhashRefresh = true;
                        return true;

                    case MintTransportRetryDecision.FailoverToSecondary:
                        Debug.LogWarning(
                            $"[OwnerGovernedAssetLedgerService] Transport error while sending mint transaction on primary RPC. " +
                            $"Failing over to secondary endpoint. exceptionType='{exception?.GetType().Name ?? "<null>"}', message='{errorMessage}'.");
                        currentRpcClient = _secondaryRpcClient;
                        usingSecondaryRpc = true;
                        transportRetryAttempts = 0;
                        forceBlockhashRefresh = true;
                        return true;

                    default:
                        return false;
                }
            }

            while (true)
            {
                var recentBlockhash = await GetRecentBlockhashAsync(currentRpcClient, forceBlockhashRefresh).ConfigureAwait(false);
                forceBlockhashRefresh = false;

                var accounts = new List<AccountMeta>
                {
                    AccountMeta.ReadOnly(configAccount.Authority, false),
                    AccountMeta.Writable(registryConfigPda, false),
                    AccountMeta.Writable(mintAuthorityPda, mintAuthorityRequiresSignature),
                    AccountMeta.Writable(payerAccount.PublicKey, true),
                    AccountMeta.Writable(manifestPda, false),
                    AccountMeta.Writable(mintPubKey, false),
                    AccountMeta.Writable(recipientAta, false),
                    AccountMeta.ReadOnly(recipientPublicKey, false),
                    AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
                    AccountMeta.ReadOnly(AssociatedTokenAccountProgram.ProgramIdKey, false),
                    AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
                    AccountMeta.Writable(metadataPda, false),
                    AccountMeta.Writable(masterEditionPda, false),
                    AccountMeta.ReadOnly(_collectionMint, false),
                    AccountMeta.ReadOnly(TokenMetadataProgramId, false),
                    AccountMeta.Writable(collectionMetadataPda, false),
                    AccountMeta.Writable(collectionMasterEditionPda, false),
                    AccountMeta.ReadOnly(RentSysvarId, false),
                    AccountMeta.ReadOnly(InstructionsSysvarId, false)
                };

                AppendCreatorSignerAccounts(accounts, sanitizedCreators);

                var instruction = new TransactionInstruction
                {
                    ProgramId = _programId,
                    Keys = accounts,
                    Data = BuildMintInstruction(request, sanitizedCreators)
                };

                var instructions = BuildMintTransactionInstructions(
                    instruction,
                    _mintComputeUnitLimit,
                    _mintComputeUnitPriceMicroLamports);

                var tx = new Transaction
                {
                    FeePayer = payerAccount.PublicKey,
                    RecentBlockHash = recentBlockhash,
                    Instructions = instructions,
                    Signatures = BuildMintSigners(
                        payerAccount.PublicKey,
                        mintAuthorityPda,
                        mintAuthorityRequiresSignature,
                        sanitizedCreators,
                        permittedSignerPublicKeys)
                };

                serializedTransaction = null;
                try
                {
                    Debug.Log("[OwnerGovernedAssetLedgerService] Signing mint transaction.");
                    signedTransaction = await RunOnUnityThreadAsync(() => wallet.SignTransaction(tx)).ConfigureAwait(false);
                    serializedTransaction = Convert.ToBase64String(signedTransaction.Serialize());
                    if (firstSerializedTransaction == null)
                    {
                        firstSerializedTransaction = serializedTransaction;
                    }
                    lastSerializedTransaction = serializedTransaction;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[OwnerGovernedAssetLedgerService] Mint transaction signing failed: {ex.Message}");
                    throw CreateMintException(ex.Message, ex, serializedTransaction);
                }

                var missingSigners = FindMissingVerifiedCreatorSignatures(signedTransaction, sanitizedCreators);
                if (missingSigners.Count > 0)
                {
                    if (retriedWithDowngradedCreators)
                    {
                        LogCreatorVerificationDiagnostics(
                            CreatorVerificationErrorSubstring,
                            sanitizedCreators,
                            permittedSignerPublicKeys,
                            signedTransaction,
                            manifestPda,
                            mintPubKey);

                        foreach (var creatorKey in missingSigners)
                        {
                            Debug.LogError(
                                $"[OwnerGovernedAssetLedgerService] Verified creator '{creatorKey}' is missing a signature on the mint transaction.");
                        }

                        LogMintTransactionDebugPayload(serializedTransaction);
                        throw CreateMintException(CreatorVerificationErrorSubstring, debugContext: serializedTransaction);
                    }

                    retriedWithDowngradedCreators = true;
                    downgradedCreatorAddresses = missingSigners;

                    foreach (var creatorKey in missingSigners)
                    {
                        Debug.LogWarning(
                            $"[OwnerGovernedAssetLedgerService] Verified creator '{creatorKey}' is missing a signature on the mint transaction. Retrying with the creator downgraded to unverified.");
                    }

                    sanitizedCreators = DowngradeCreatorsForMissingSignatures(sanitizedCreators, missingSigners);
                    continue;
                }

                try
                {
                    Debug.Log(
                        $"[OwnerGovernedAssetLedgerService] Sending mint transaction to RPC. endpointRole={(usingSecondaryRpc ? "secondary" : "primary")}.");
                    sendRes = await currentRpcClient.SendTransactionAsync(
                        serializedTransaction,
                        skipPreflight: false,
                        preFlightCommitment: Commitment.Confirmed).ConfigureAwait(false);
                    transportRetryAttempts = 0;
                }
                catch (Exception ex)
                {
                    if (!forcedCreatorDowngradeAfterProgramError && ContainsCreatorVerificationError(ex.Message))
                    {
                        var downgradeLogMessage =
                            "[OwnerGovernedAssetLedgerService] Mint transaction returned custom program error 0x36 during submission. " +
                            "Retrying with all creators downgraded to unverified.";
                        if (!string.IsNullOrWhiteSpace(ex.Message))
                        {
                            downgradeLogMessage += $" reason='{ex.Message}'.";
                        }

                        Debug.LogWarning(downgradeLogMessage);

                        retriedWithDowngradedCreators = true;
                        forcedCreatorDowngradeAfterProgramError = true;
                        transportRetryAttempts = 0;

                        var forcedDowngradedCreators = DowngradeAllCreatorsToUnverified(sanitizedCreators, out var forcedDowngradedAddresses);
                        sanitizedCreators = forcedDowngradedCreators;
                        MergeDowngradedCreatorAddresses(ref downgradedCreatorAddresses, forcedDowngradedAddresses);

                        sendRes = null;
                        continue;
                    }

                    if (await HandleTransportFailureAsync(ex.Message, ex).ConfigureAwait(false))
                    {
                        sendRes = null;
                        continue;
                    }

                    Debug.LogError($"[OwnerGovernedAssetLedgerService] Mint transaction failed to send: {ex.Message}");
                    LogCreatorVerificationDiagnostics(
                        ex.Message,
                        sanitizedCreators,
                        permittedSignerPublicKeys,
                        signedTransaction,
                        manifestPda,
                        mintPubKey);
                    LogMintTransactionDebugPayload(serializedTransaction);
                    throw CreateMintException(ex.Message, ex, serializedTransaction);
                }

                if ((sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result)) &&
                    await HandleTransportFailureAsync(sendRes?.Reason, null).ConfigureAwait(false))
                {
                    sendRes = null;
                    continue;
                }

                if ((sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result)) &&
                    !forcedCreatorDowngradeAfterProgramError &&
                    RequestContainsCreatorVerificationError(sendRes))
                {
                    var downgradeReason = ExtractRequestReason(sendRes);
                    var downgradeLogMessage =
                        "[OwnerGovernedAssetLedgerService] Mint transaction returned custom program error 0x36 during submission. " +
                        "Retrying with all creators downgraded to unverified.";
                    if (!string.IsNullOrWhiteSpace(downgradeReason))
                    {
                        downgradeLogMessage += $" reason='{downgradeReason}'.";
                    }

                    Debug.LogWarning(downgradeLogMessage);

                    retriedWithDowngradedCreators = true;
                    forcedCreatorDowngradeAfterProgramError = true;
                    transportRetryAttempts = 0;

                    var forcedDowngradedCreators = DowngradeAllCreatorsToUnverified(sanitizedCreators, out var forcedDowngradedAddresses);
                    sanitizedCreators = forcedDowngradedCreators;
                    MergeDowngradedCreatorAddresses(ref downgradedCreatorAddresses, forcedDowngradedAddresses);

                    sendRes = null;
                    continue;
                }

                break;
            }

            serializedTransaction = lastSerializedTransaction;

            if (sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result))
            {
                var reason = ExtractRequestReason(sendRes);
                Debug.LogError(
                    $"[OwnerGovernedAssetLedgerService] Mint transaction failed to send. reason='{reason}'.");
                LogCreatorVerificationDiagnostics(
                    reason,
                    sanitizedCreators,
                    permittedSignerPublicKeys,
                    signedTransaction,
                    manifestPda,
                    mintPubKey);
                if (!string.IsNullOrWhiteSpace(firstSerializedTransaction))
                {
                    LogMintTransactionDebugPayload(firstSerializedTransaction);
                }

                if (!string.IsNullOrWhiteSpace(serializedTransaction) &&
                    !string.Equals(firstSerializedTransaction, serializedTransaction, StringComparison.Ordinal))
                {
                    LogMintTransactionDebugPayload(serializedTransaction);
                }

                var guardRailKey = GetCollectionGuardRailKey(reason);
                if (!string.IsNullOrEmpty(guardRailKey))
                {
                    var authorityException = await CreateCollectionGuardRailExceptionAsync(
                        guardRailKey,
                        collectionMetadataPda,
                        collectionMasterEditionPda,
                        mintAuthorityPda,
                        reason,
                        serializedTransaction).ConfigureAwait(false);

                    throw authorityException;
                }

                throw CreateMintException(reason, debugContext: serializedTransaction);
            }

            if (retriedWithDowngradedCreators && downgradedCreatorAddresses != null && downgradedCreatorAddresses.Count > 0)
            {
                Debug.LogWarning(
                    $"[OwnerGovernedAssetLedgerService] Mint transaction proceeded with unverified creators: {string.Join(", ", downgradedCreatorAddresses)}. Prompt the affected creators to reconnect and sign if verification was expected.");
            }

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Mint transaction succeeded. mintAddress='{mintPubKey}', signature='{sendRes.Result}'.");
            return new OwnerGovernedAssetLedgerMintResult(mintPubKey.Key, sendRes.Result, retriedWithDowngradedCreators);
        }

        private static List<TransactionInstruction> BuildMintTransactionInstructions(
            TransactionInstruction mintInstruction,
            uint computeUnitLimit,
            ulong? computeUnitPriceMicroLamports)
        {
            if (mintInstruction == null)
                throw new ArgumentNullException(nameof(mintInstruction));

            var instructions = new List<TransactionInstruction>();

            if (computeUnitLimit > 0)
            {
                instructions.Add(ComputeBudgetProgram.SetComputeUnitLimit(computeUnitLimit));
            }

            if (computeUnitPriceMicroLamports.HasValue && computeUnitPriceMicroLamports.Value > 0)
            {
                instructions.Add(ComputeBudgetProgram.SetComputeUnitPrice(computeUnitPriceMicroLamports.Value));
            }

            instructions.Add(mintInstruction);

            return instructions;
        }

        public async Task<string> UpdateManifestAsync(
            ulong objectId,
            string objectMintAddress,
            string ownerTokenAccountAddress,
            byte[] manifestHash,
            string metadataUri,
            bool isActive)
        {
            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] UpdateManifestAsync called. objectId={objectId}, mint='{objectMintAddress}', ownerTokenAccount='{ownerTokenAccountAddress}', " +
                $"metadataUri='{metadataUri}', isActive={isActive}.");
            var wallet = _walletResolver() ?? throw new InvalidOperationException("Wallet not connected");
            var payerAccount = wallet.Account ?? throw new InvalidOperationException("Wallet account unavailable");

            if (manifestHash == null)
                throw new ArgumentNullException(nameof(manifestHash));
            if (manifestHash.Length != 32)
                throw new ArgumentException("Manifest hash must be exactly 32 bytes.", nameof(manifestHash));
            if (string.IsNullOrWhiteSpace(objectMintAddress))
                throw new ArgumentException("Object mint address is required for manifest updates.", nameof(objectMintAddress));

            var registryConfigPda = ResolveRegistryConfigForRead();
            var manifestPda = DeriveManifestPda(registryConfigPda, objectId, null);
            var objectMintPubKey = new PublicKey(objectMintAddress);
            var authPda = DeriveMintAuthorityPda(registryConfigPda, null);
            var metadataPda = DeriveMetadataPda(objectMintPubKey);

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Derived update PDAs. manifest='{manifestPda}', auth='{authPda}', metadata='{metadataPda}'.");

            var derivedOwnerAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(
                payerAccount.PublicKey,
                objectMintPubKey);

            var ownerTokenAccount = await ResolveOwnerTokenAccountAsync(
                derivedOwnerAta,
                ownerTokenAccountAddress,
                payerAccount.PublicKey,
                objectMintPubKey).ConfigureAwait(false);
            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Resolved owner ATA. derived='{derivedOwnerAta}', resolved='{ownerTokenAccount}'.");

            var accounts = BuildUpdateManifestAccountList(
                payerAccount.PublicKey,
                registryConfigPda,
                authPda,
                manifestPda,
                objectMintPubKey,
                ownerTokenAccount,
                metadataPda);

            var instruction = new TransactionInstruction
            {
                ProgramId = _programId,
                Keys = accounts,
                Data = BuildUpdateInstruction(manifestHash, metadataUri, isActive)
            };

            var recentBlockhash = await GetRecentBlockhashAsync().ConfigureAwait(false);

            var txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(recentBlockhash)
                .SetFeePayer(payerAccount)
                .AddInstruction(instruction);

            var builtTx = txBuilder.Build(new List<Account> { payerAccount });
            var tx = Transaction.Deserialize(builtTx);

            Transaction signedTransaction;
            try
            {
                Debug.Log("[OwnerGovernedAssetLedgerService] Signing update transaction.");
                signedTransaction = await RunOnUnityThreadAsync(() => wallet.SignTransaction(tx)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Update transaction signing failed: {ex.Message}");
                throw CreateUpdateException(ex.Message, ex);
            }

            RequestResult<string> sendRes;
            try
            {
                Debug.Log("[OwnerGovernedAssetLedgerService] Sending update transaction to RPC.");
                sendRes = await _rpcClient.SendTransactionAsync(
                    Convert.ToBase64String(signedTransaction.Serialize()),
                    skipPreflight: false,
                    preFlightCommitment: Commitment.Confirmed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Update transaction failed to send: {ex.Message}");
                throw CreateUpdateException(ex.Message, ex);
            }

            if (sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result))
            {
                var reason = sendRes?.Reason;
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Update transaction failed to send. reason='{reason}'.");
                throw CreateUpdateException(reason);
            }

            Debug.Log($"[OwnerGovernedAssetLedgerService] Update transaction succeeded. signature='{sendRes.Result}'.");
            return sendRes.Result;
        }

        public async Task<string> SetAuthorityAsync(
            string newAuthorityPublicKey,
            string requestNamespace = null,
            byte? expectedConfigBump = null)
        {
            if (string.IsNullOrWhiteSpace(newAuthorityPublicKey))
                throw new ArgumentException("New authority public key is required.", nameof(newAuthorityPublicKey));

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] SetAuthorityAsync called. newAuthority='{newAuthorityPublicKey}', namespace='{requestNamespace}'.");

            var wallet = _walletResolver() ?? throw new InvalidOperationException("Wallet not connected");
            var authorityAccount = wallet.Account ?? throw new InvalidOperationException("Wallet account unavailable");

            var registryConfigPda = ResolveRegistryConfigAccount(requestNamespace, expectedConfigBump);
            var newAuthority = new PublicKey(newAuthorityPublicKey);

            var instruction = new TransactionInstruction
            {
                ProgramId = _programId,
                Keys = new List<AccountMeta>
                {
                    AccountMeta.ReadOnly(authorityAccount.PublicKey, true),
                    AccountMeta.Writable(registryConfigPda, false)
                },
                Data = BuildSetAuthorityInstruction(newAuthority)
            };

            var signature = await SendAdministrativeTransactionAsync(
                wallet,
                authorityAccount,
                instruction,
                "set_authority",
                UnknownAuthorityUpdateErrorMessage).ConfigureAwait(false);

            Debug.Log($"[OwnerGovernedAssetLedgerService] set_authority transaction succeeded. signature='{signature}'.");
            return signature;
        }

        public async Task<string> SetPausedAsync(
            bool paused,
            string requestNamespace = null,
            byte? expectedConfigBump = null)
        {
            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] SetPausedAsync called. paused={paused}, namespace='{requestNamespace}'.");

            var wallet = _walletResolver() ?? throw new InvalidOperationException("Wallet not connected");
            var authorityAccount = wallet.Account ?? throw new InvalidOperationException("Wallet account unavailable");

            var registryConfigPda = ResolveRegistryConfigAccount(requestNamespace, expectedConfigBump);

            var instruction = new TransactionInstruction
            {
                ProgramId = _programId,
                Keys = new List<AccountMeta>
                {
                    AccountMeta.ReadOnly(authorityAccount.PublicKey, true),
                    AccountMeta.Writable(registryConfigPda, false)
                },
                Data = BuildSetPausedInstruction(paused)
            };

            var signature = await SendAdministrativeTransactionAsync(
                wallet,
                authorityAccount,
                instruction,
                "set_paused",
                UnknownPauseUpdateErrorMessage).ConfigureAwait(false);

            Debug.Log($"[OwnerGovernedAssetLedgerService] set_paused transaction succeeded. signature='{signature}'.");
            return signature;
        }

        public async Task<string> MigrateConfigNamespaceAsync(OwnerGovernedAssetLedgerMigrationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] MigrateConfigNamespaceAsync called. targetNamespace='{request.NewNamespace}'.");

            var wallet = _walletResolver() ?? throw new InvalidOperationException("Wallet not connected");
            var authorityAccount = wallet.Account ?? throw new InvalidOperationException("Wallet account unavailable");

            var trimmedNamespace = request.NewNamespace?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedNamespace))
                throw new ArgumentException("New namespace public key is required.", nameof(request));

            PublicKey newNamespaceKey;
            try
            {
                newNamespaceKey = new PublicKey(trimmedNamespace);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Unable to parse the new namespace public key.", nameof(request), ex);
            }

            var registryConfigPda = ResolveRegistryConfigForRead();
            var configAccount = await FetchRegistryConfigAsync(registryConfigPda).ConfigureAwait(false);

            if (!authorityAccount.PublicKey.Equals(configAccount.Authority))
            {
                throw new InvalidOperationException(
                    $"The connected wallet ({authorityAccount.PublicKey}) is not the registry authority ({configAccount.Authority}).");
            }

            ValidateExpectedPda(request.ExpectedOldConfigPda, registryConfigPda, "old config", nameof(request.ExpectedOldConfigPda));
            var oldAuthPda = DeriveMintAuthorityPda(registryConfigPda, configAccount.AuthBump);
            ValidateExpectedPda(request.ExpectedOldAuthPda, oldAuthPda, "old auth", nameof(request.ExpectedOldAuthPda));

            var newConfigPda = DeriveRegistryConfigPda(newNamespaceKey, null);
            ValidateExpectedPda(request.ExpectedNewConfigPda, newConfigPda, "new config", nameof(request.ExpectedNewConfigPda));
            var newAuthPda = DeriveMintAuthorityPda(newConfigPda, null);
            ValidateExpectedPda(request.ExpectedNewAuthPda, newAuthPda, "new auth", nameof(request.ExpectedNewAuthPda));

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Prepared namespace migration PDAs. oldConfig='{registryConfigPda}', oldAuth='{oldAuthPda}', " +
                $"newConfig='{newConfigPda}', newAuth='{newAuthPda}'.");

            var accounts = new List<AccountMeta>
            {
                AccountMeta.Writable(authorityAccount.PublicKey, true),
                AccountMeta.Writable(registryConfigPda, false),
                AccountMeta.Writable(newConfigPda, false),
                AccountMeta.ReadOnly(oldAuthPda, false),
                AccountMeta.Writable(newAuthPda, false),
                AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
            };

            var instruction = new TransactionInstruction
            {
                ProgramId = _programId,
                Keys = accounts,
                Data = BuildMigrateConfigNamespaceInstruction(newNamespaceKey)
            };

            var recentBlockhash = await GetRecentBlockhashAsync().ConfigureAwait(false);

            var txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(recentBlockhash)
                .SetFeePayer(authorityAccount)
                .AddInstruction(instruction);

            var builtTx = txBuilder.Build(new List<Account> { authorityAccount });
            var tx = Transaction.Deserialize(builtTx);

            Transaction signedTransaction;
            try
            {
                Debug.Log("[OwnerGovernedAssetLedgerService] Signing namespace migration transaction.");
                signedTransaction = await RunOnUnityThreadAsync(() => wallet.SignTransaction(tx)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Namespace migration transaction signing failed: {ex.Message}");
                throw CreateMigrationException(ex.Message, ex);
            }

            var serializedTransaction = Convert.ToBase64String(signedTransaction.Serialize());

            RequestResult<string> sendRes;
            try
            {
                Debug.Log("[OwnerGovernedAssetLedgerService] Sending namespace migration transaction to RPC.");
                sendRes = await _rpcClient.SendTransactionAsync(
                    serializedTransaction,
                    skipPreflight: false,
                    preFlightCommitment: Commitment.Confirmed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Namespace migration transaction failed to send: {ex.Message}");
                throw CreateMigrationException(ex.Message, ex, serializedTransaction);
            }

            if (sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result))
            {
                var reason = sendRes?.Reason;
                Debug.LogError(
                    $"[OwnerGovernedAssetLedgerService] Namespace migration transaction failed to send. reason='{reason}'.");
                throw CreateMigrationException(reason, debugContext: serializedTransaction);
            }

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Namespace migration succeeded. newNamespace='{newNamespaceKey}', newConfig='{newConfigPda}', signature='{sendRes.Result}'.");
            return sendRes.Result;
        }

        private Task<string> GetRecentBlockhashAsync(bool forceRefresh = false)
        {
            return GetRecentBlockhashAsync(_rpcClient, forceRefresh);
        }

        private async Task<string> GetRecentBlockhashAsync(IRpcClient rpcClient, bool forceRefresh)
        {
            if (rpcClient == null)
                throw new ArgumentNullException(nameof(rpcClient));

            if (!forceRefresh && _blockhashMaxSeconds > 0)
            {
                lock (_blockhashCacheLock)
                {
                    if (_cachedBlockhashes.TryGetValue(rpcClient, out var cached) &&
                        cached != null &&
                        !string.IsNullOrEmpty(cached.Value) &&
                        (DateTime.UtcNow - cached.Timestamp).TotalSeconds < _blockhashMaxSeconds)
                    {
                        return cached.Value;
                    }
                }
            }

            var blockhashResponse = await rpcClient
                .GetLatestBlockHashAsync(Commitment.Confirmed)
                .ConfigureAwait(false);
            var blockhash = blockhashResponse?.Result?.Value?.Blockhash;

            if (string.IsNullOrEmpty(blockhash))
            {
                return blockhash;
            }

            if (_blockhashMaxSeconds > 0)
            {
                lock (_blockhashCacheLock)
                {
                    _cachedBlockhashes[rpcClient] = new BlockhashCacheEntry
                    {
                        Value = blockhash,
                        Timestamp = DateTime.UtcNow
                    };
                }
            }

            return blockhash;
        }

        private async Task<string> SendAdministrativeTransactionAsync(
            WalletBase wallet,
            Account authorityAccount,
            TransactionInstruction instruction,
            string actionName,
            string fallbackUserMessage)
        {
            if (wallet == null)
                throw new ArgumentNullException(nameof(wallet));
            if (authorityAccount == null)
                throw new ArgumentNullException(nameof(authorityAccount));
            if (instruction == null)
                throw new ArgumentNullException(nameof(instruction));
            if (string.IsNullOrWhiteSpace(actionName))
                throw new ArgumentException("Action name is required.", nameof(actionName));
            if (string.IsNullOrWhiteSpace(fallbackUserMessage))
                throw new ArgumentException("Fallback message is required.", nameof(fallbackUserMessage));

            var recentBlockhash = await GetRecentBlockhashAsync().ConfigureAwait(false);

            var txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(recentBlockhash)
                .SetFeePayer(authorityAccount)
                .AddInstruction(instruction);

            var builtTx = txBuilder.Build(new List<Account> { authorityAccount });
            var tx = Transaction.Deserialize(builtTx);

            Transaction signedTransaction;
            try
            {
                Debug.Log($"[OwnerGovernedAssetLedgerService] Signing {actionName} transaction.");
                signedTransaction = await RunOnUnityThreadAsync(() => wallet.SignTransaction(tx)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] {actionName} transaction signing failed: {ex.Message}");
                throw CreateAdministrativeException(fallbackUserMessage, ex.Message, ex);
            }

            RequestResult<string> sendRes;
            try
            {
                Debug.Log($"[OwnerGovernedAssetLedgerService] Sending {actionName} transaction to RPC.");
                sendRes = await _rpcClient.SendTransactionAsync(
                    Convert.ToBase64String(signedTransaction.Serialize()),
                    skipPreflight: false,
                    preFlightCommitment: Commitment.Confirmed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] {actionName} transaction failed to send: {ex.Message}");
                throw CreateAdministrativeException(fallbackUserMessage, ex.Message, ex);
            }

            if (sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result))
            {
                var reason = sendRes?.Reason;
                Debug.LogError(
                    $"[OwnerGovernedAssetLedgerService] {actionName} transaction failed to send. reason='{reason}'.");
                throw CreateAdministrativeException(fallbackUserMessage, reason);
            }

            return sendRes.Result;
        }

        protected virtual async Task<PublicKey> ResolveOwnerTokenAccountAsync(
            PublicKey derivedOwnerAta,
            string ownerTokenAccountAddress,
            PublicKey payerPublicKey,
            PublicKey objectMintPubKey)
        {
            if (derivedOwnerAta == null)
                throw new ArgumentNullException(nameof(derivedOwnerAta));
            if (payerPublicKey == null)
                throw new ArgumentNullException(nameof(payerPublicKey));
            if (objectMintPubKey == null)
                throw new ArgumentNullException(nameof(objectMintPubKey));

            if (string.IsNullOrWhiteSpace(ownerTokenAccountAddress))
            {
                return derivedOwnerAta;
            }

            var ownerTokenAccount = new PublicKey(ownerTokenAccountAddress);

            if (string.Equals(ownerTokenAccount.Key, derivedOwnerAta.Key, StringComparison.Ordinal))
            {
                return derivedOwnerAta;
            }

            await ValidateOwnerTokenAccountAsync(
                ownerTokenAccount,
                payerPublicKey,
                objectMintPubKey).ConfigureAwait(false);

            return ownerTokenAccount;
        }

        protected virtual async Task ValidateOwnerTokenAccountAsync(
            PublicKey ownerTokenAccount,
            PublicKey expectedOwner,
            PublicKey expectedMint)
        {
            if (ownerTokenAccount == null)
                throw new ArgumentNullException(nameof(ownerTokenAccount));
            if (expectedOwner == null)
                throw new ArgumentNullException(nameof(expectedOwner));
            if (expectedMint == null)
                throw new ArgumentNullException(nameof(expectedMint));

            RequestResult<ResponseValue<TokenAccountInfo>> tokenAccountResult;
            try
            {
                tokenAccountResult = await _rpcClient.GetTokenAccountInfoAsync(ownerTokenAccount.Key, Commitment.Confirmed);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Failed to fetch token account info for {ownerTokenAccount.Key}: {ex.Message}");
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to verify the provided object token account. Please try again or use the default account.",
                    ex.Message,
                    ex);
            }

            if (tokenAccountResult == null || !tokenAccountResult.WasSuccessful || tokenAccountResult.Result?.Value == null)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Token account lookup failed for {ownerTokenAccount.Key}: {tokenAccountResult?.Reason}");
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to verify the provided object token account. Please ensure the account exists and try again.",
                    tokenAccountResult?.Reason);
            }

            var tokenAccountInfo = tokenAccountResult.Result.Value;
            var tokenOwner = tokenAccountInfo.Owner;
            var tokenMint = tokenAccountInfo.Data.Parsed.Info.Mint;

            if (string.IsNullOrWhiteSpace(tokenOwner) || string.IsNullOrWhiteSpace(tokenMint))
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Token account {ownerTokenAccount.Key} returned incomplete data. Owner: {tokenOwner ?? "<null>"}, Mint: {tokenMint ?? "<null>"}.");
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to verify the provided object token account due to incomplete data.",
                    "Token account info missing owner or mint.");
            }

            if (!string.Equals(tokenOwner, expectedOwner.Key, StringComparison.Ordinal))
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Provided token account {ownerTokenAccount.Key} is owned by {tokenOwner}, expected {expectedOwner.Key}.");
                throw new OwnerGovernedAssetLedgerException(
                    "The connected wallet does not own the provided object token account.",
                    "Provided token account owner does not match connected wallet.");
            }

            if (!string.Equals(tokenMint, expectedMint.Key, StringComparison.Ordinal))
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] Provided token account {ownerTokenAccount.Key} has mint {tokenMint}, expected {expectedMint.Key}.");
                throw new OwnerGovernedAssetLedgerException(
                    "The provided token account does not hold the selected object NFT.",
                    "Provided token account mint does not match object mint.");
            }
        }

        public async Task<ObjectManifestAccount> GetManifestAsync(ulong objectId)
        {
            var registryConfigPda = ResolveRegistryConfigForRead();
            var manifestPda = DeriveManifestPda(registryConfigPda, objectId, null);

            var acctRes = await _rpcClient.GetAccountInfoAsync(manifestPda.Key, Commitment.Confirmed);
            if (!acctRes.WasSuccessful || acctRes.Result.Value == null)
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the requested object manifest. Please verify your Solana configuration.",
                    acctRes?.Reason);
            }

            var encodedData = acctRes.Result.Value.Data;
            if (encodedData == null || encodedData.Count == 0 || string.IsNullOrEmpty(encodedData[0]))
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the requested object manifest. Please verify your Solana configuration.",
                    "Manifest account returned no data.");
            }

            var data = Convert.FromBase64String(encodedData[0]);
            var owner = acctRes.Result.Value.Owner;
            if (!string.Equals(owner, _programId.Key, StringComparison.Ordinal))
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the requested object manifest. Please verify your Solana configuration.",
                    $"Manifest owner mismatch. Expected {_programId.Key}, received {owner ?? "<null>"}.\nAccount: {manifestPda}");
            }

            if (data.Length < ManifestAccountDiscriminator.Length)
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the requested object manifest. Please verify your Solana configuration.",
                    $"Manifest data too short to verify discriminator. Length {data.Length}.");
            }

            if (!data.AsSpan(0, ManifestAccountDiscriminator.Length).SequenceEqual(ManifestAccountDiscriminator))
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the requested object manifest. Please verify your Solana configuration.",
                    $"Manifest discriminator mismatch. Expected {ToHexString(ManifestAccountDiscriminator)}, received {ToHexString(data.AsSpan(0, ManifestAccountDiscriminator.Length))}.\nAccount: {manifestPda}");
            }

            return ObjectManifestAccount.Deserialize(data, manifestPda);
        }

        private PublicKey ResolveRegistryConfigAccount(string requestNamespace, byte? expectedBump)
        {
            if (_registryConfigOverride != null)
            {
                return _registryConfigOverride;
            }

            var namespaceKey = ResolveNamespace(requestNamespace);
            Debug.Log($"[OwnerGovernedAssetLedgerService] Resolved Namespace: {namespaceKey}");
            return DeriveRegistryConfigPda(namespaceKey, expectedBump);
        }

        private PublicKey ResolveMintAuthorityAccount(PublicKey registryConfig, byte? expectedBump)
        {
            if (_mintAuthorityOverride != null)
            {
                return _mintAuthorityOverride;
            }

            return DeriveMintAuthorityPda(registryConfig, expectedBump);
        }

        private async Task ValidateCollectionUpdateAuthorityAsync(
            PublicKey collectionMetadataPda,
            PublicKey collectionMasterEditionPda,
            PublicKey expectedAuthority)
        {
            CollectionMetadataInfo metadataInfo;
            try
            {
                metadataInfo = await FetchCollectionMetadataInfoAsync(collectionMetadataPda).ConfigureAwait(false);
            }
            catch (OwnerGovernedAssetLedgerException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[OwnerGovernedAssetLedgerService] Failed to fetch collection metadata account '{collectionMetadataPda}': {ex.Message}");
                throw new OwnerGovernedAssetLedgerException(CollectionMetadataUnavailableMessage, ex.Message, ex);
            }

            var actualAuthority = metadataInfo.UpdateAuthority;
            if (!actualAuthority.Equals(expectedAuthority))
            {
                var rawMessage =
                    $"Collection update authority mismatch. metadata='{collectionMetadataPda}', expected='{expectedAuthority}', actual='{actualAuthority}'.";
                Debug.LogError($"[OwnerGovernedAssetLedgerService] {rawMessage}");
                throw new OwnerGovernedAssetLedgerException(CollectionAuthorityMismatchMessage, rawMessage);
            }

            var isSizedCollection = metadataInfo.IsSizedCollection;
            var shouldEnforceUniqueMasterEdition = ShouldEnforceUniqueMasterEdition(isSizedCollection);
            var sizedStateLog = isSizedCollection.HasValue ? (isSizedCollection.Value ? "true" : "false") : "<null>";
            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Collection metadata update authority validated. metadata='{collectionMetadataPda}', authority='{actualAuthority}', isSizedCollection={sizedStateLog}, enforceUniqueMasterEdition={(shouldEnforceUniqueMasterEdition ? "true" : "false")}.");

            if (!shouldEnforceUniqueMasterEdition)
            {
                Debug.Log(
                    "[OwnerGovernedAssetLedgerService] Skipping collection master edition uniqueness validation because the metadata contains collection_details (sized collection).");
                return;
            }

            CollectionMasterEditionInfo masterEditionInfo;
            try
            {
                masterEditionInfo = await FetchCollectionMasterEditionInfoAsync(collectionMasterEditionPda).ConfigureAwait(false);
            }
            catch (OwnerGovernedAssetLedgerException ex)
            {
                Debug.LogError($"[OwnerGovernedAssetLedgerService] {ex.RawMessage ?? ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[OwnerGovernedAssetLedgerService] Failed to fetch collection master edition account '{collectionMasterEditionPda}': {ex.Message}");
                throw new OwnerGovernedAssetLedgerException(CollectionMasterEditionUnavailableMessage, ex.Message, ex);
            }

            if (!masterEditionInfo.IsUnique)
            {
                var rawMessage =
                    $"The collection master edition must have max supply 0. masterEdition='{collectionMasterEditionPda}', supply={masterEditionInfo.Supply.ToString(CultureInfo.InvariantCulture)}, maxSupplyOption={masterEditionInfo.MaxSupplyOption.ToString(CultureInfo.InvariantCulture)}, maxSupply={(masterEditionInfo.MaxSupply.HasValue ? masterEditionInfo.MaxSupply.Value.ToString(CultureInfo.InvariantCulture) : "<null>")}.";
                Debug.LogError($"[OwnerGovernedAssetLedgerService] {rawMessage}");
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMasterEditionNotUniqueMessage,
                    rawMessage,
                    debugContext: CreateMasterEditionDebugPayload(masterEditionInfo));
            }

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Collection master edition validated. masterEdition='{collectionMasterEditionPda}', supply={masterEditionInfo.Supply}, maxSupplyOption={masterEditionInfo.MaxSupplyOption}, maxSupply={(masterEditionInfo.MaxSupply.HasValue ? masterEditionInfo.MaxSupply.Value.ToString(CultureInfo.InvariantCulture) : "<null>")}, isUnique={(masterEditionInfo.IsUnique ? "true" : "false")}.");
        }

        private async Task<CollectionMetadataInfo> FetchCollectionMetadataInfoAsync(PublicKey collectionMetadataPda)
        {
            var accountInfo = await _rpcClient.GetAccountInfoAsync(collectionMetadataPda.Key, Commitment.Confirmed)
                .ConfigureAwait(false);
            if (!accountInfo.WasSuccessful || accountInfo.Result?.Value == null)
            {
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMetadataUnavailableMessage,
                    accountInfo?.Reason ?? "Collection metadata account lookup failed.");
            }

            var data = accountInfo.Result.Value.Data;
            if (data == null || data.Count == 0 || string.IsNullOrEmpty(data[0]))
            {
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMetadataUnavailableMessage,
                    "Collection metadata account returned no data.");
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(data[0]);
            }
            catch (FormatException ex)
            {
                throw new OwnerGovernedAssetLedgerException(CollectionMetadataUnavailableMessage, ex.Message, ex);
            }

            if (raw.Length < MetadataUpdateAuthorityOffset + PublicKeyLength)
            {
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMetadataUnavailableMessage,
                    $"Collection metadata account is truncated. Length={raw.Length}.");
            }

            var updateAuthorityBytes = new byte[PublicKeyLength];
            Buffer.BlockCopy(raw, MetadataUpdateAuthorityOffset, updateAuthorityBytes, 0, PublicKeyLength);

            var isSizedCollection = TryDetectCollectionSizedState(raw);
            if (!isSizedCollection.HasValue)
            {
                Debug.LogWarning(
                    $"[OwnerGovernedAssetLedgerService] Unable to determine collection sizing state for metadata '{collectionMetadataPda}'.");
            }

            return new CollectionMetadataInfo(new PublicKey(updateAuthorityBytes), isSizedCollection);
        }

        private static bool ShouldEnforceUniqueMasterEdition(bool? isSizedCollection)
        {
            return isSizedCollection != true;
        }

        private async Task<CollectionMasterEditionInfo> FetchCollectionMasterEditionInfoAsync(
            PublicKey collectionMasterEditionPda)
        {
            var accountInfo = await _rpcClient.GetAccountInfoAsync(collectionMasterEditionPda.Key, Commitment.Confirmed)
                .ConfigureAwait(false);
            if (!accountInfo.WasSuccessful || accountInfo.Result?.Value == null)
            {
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMasterEditionUnavailableMessage,
                    accountInfo?.Reason ?? "Collection master edition account lookup failed.");
            }

            var owner = accountInfo.Result.Value.Owner;
            if (!string.Equals(owner, TokenMetadataProgramId.Key, StringComparison.Ordinal))
            {
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMasterEditionUnavailableMessage,
                    $"Collection master edition owner mismatch. Expected {TokenMetadataProgramId.Key}, received {owner ?? "<null>"}.\nAccount: {collectionMasterEditionPda}");
            }

            var data = accountInfo.Result.Value.Data;
            if (data == null || data.Count == 0 || string.IsNullOrEmpty(data[0]))
            {
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMasterEditionUnavailableMessage,
                    "Collection master edition account returned no data.");
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(data[0]);
            }
            catch (FormatException ex)
            {
                throw new OwnerGovernedAssetLedgerException(CollectionMasterEditionUnavailableMessage, ex.Message, ex);
            }

            if (!TryParseMasterEditionLayout(raw, out var layout))
            {
                var rawMessage =
                    $"Failed to parse collection master edition account '{collectionMasterEditionPda}'.";
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMasterEditionNotUniqueMessage,
                    rawMessage);
            }

            var info = new CollectionMasterEditionInfo(
                collectionMasterEditionPda,
                layout.Discriminator,
                layout.Supply,
                layout.MaxSupplyOption,
                layout.MaxSupply);

            if (layout.Discriminator != MasterEditionV1Discriminator && layout.Discriminator != MasterEditionV2Discriminator)
            {
                var rawMessage =
                    $"Collection master edition discriminator {layout.Discriminator} is not recognized as MasterEditionV1 ({MasterEditionV1Discriminator}) or MasterEditionV2 ({MasterEditionV2Discriminator}).";
                throw new OwnerGovernedAssetLedgerException(
                    CollectionMasterEditionNotUniqueMessage,
                    rawMessage,
                    debugContext: CreateMasterEditionDebugPayload(info));
            }

            return info;
        }

        private async Task<OwnerGovernedAssetLedgerException> CreateCollectionGuardRailExceptionAsync(
            string guardRailKey,
            PublicKey collectionMetadataPda,
            PublicKey collectionMasterEditionPda,
            PublicKey expectedAuthority,
            string rawMessage,
            string serializedTransaction = null)
        {
            if (!AnchorErrorFriendlyMessages.TryGetValue(
                    guardRailKey,
                    out var friendly))
            {
                friendly = UnknownMintErrorMessage;
            }

            string masterEditionDebug = null;
            if (!string.IsNullOrEmpty(guardRailKey) &&
                string.Equals(guardRailKey, CollectionMasterEditionGuardRailErrorSubstring, StringComparison.OrdinalIgnoreCase) &&
                collectionMasterEditionPda != null)
            {
                try
                {
                    var masterEditionInfo = await FetchCollectionMasterEditionInfoAsync(
                            collectionMasterEditionPda)
                        .ConfigureAwait(false);
                    masterEditionDebug = CreateMasterEditionDebugPayload(masterEditionInfo);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[OwnerGovernedAssetLedgerService] Unable to fetch collection master edition diagnostics after guard rail failure: {ex.Message}.");
                }
            }

            var debugContext = CombineDebugContexts(serializedTransaction, masterEditionDebug);

            CollectionMetadataInfo metadataInfo = null;
            PublicKey actualAuthority = null;
            try
            {
                metadataInfo = await FetchCollectionMetadataInfoAsync(collectionMetadataPda).ConfigureAwait(false);
                actualAuthority = metadataInfo.UpdateAuthority;
            }
            catch (OwnerGovernedAssetLedgerException metadataException)
            {
                Debug.LogWarning(
                    $"[OwnerGovernedAssetLedgerService] Unable to refetch collection metadata after guard rail failure: {metadataException.RawMessage ?? metadataException.Message}.");
                var combinedRaw = CombineRawMessages(rawMessage, metadataException.RawMessage);
                var composedMessage =
                    $"{friendly}\nAdditionally, the client could not confirm the collection update authority because the metadata account was unavailable. Please verify the collection metadata account and try again.";
                var combinedDebug = CombineDebugContexts(debugContext, metadataException.DebugContext);
                return new OwnerGovernedAssetLedgerException(composedMessage, combinedRaw, metadataException, combinedDebug);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[OwnerGovernedAssetLedgerService] Unexpected error while refetching collection metadata after guard rail failure: {ex.Message}.");
                return new OwnerGovernedAssetLedgerException(friendly, rawMessage, ex, debugContext);
            }

            if (actualAuthority == null)
            {
                return new OwnerGovernedAssetLedgerException(friendly, rawMessage, debugContext: debugContext);
            }

            if (!actualAuthority.Equals(expectedAuthority))
            {
                Debug.LogError(
                    $"[OwnerGovernedAssetLedgerService] Mint guard rail triggered. collectionMetadata='{collectionMetadataPda}', expectedAuthority='{expectedAuthority}', actualAuthority='{actualAuthority}'.");

                var detailedMessage =
                    $"{friendly}\n\nCurrent update authority: {actualAuthority}\nExpected mint authority: {expectedAuthority}\nAsk the studio team to rotate the collection's update authority before minting new levels.";

                return new OwnerGovernedAssetLedgerException(detailedMessage, rawMessage, debugContext: debugContext);
            }

            if (metadataInfo?.IsSizedCollection == false)
            {
                Debug.LogWarning(
                    $"[OwnerGovernedAssetLedgerService] Guard rail triggered on an unsized collection. metadata='{collectionMetadataPda}'. This usually indicates the deployed OGAL program is still using the legacy sized-only verification path.");

                var upgradeHint =
                    "The collection metadata reports that it is an unsized collection. Legacy OGAL deployments called the sized collection verifier unconditionally and will always return error 0x65 for unsized collections. Make sure the on-chain program has been upgraded to the October 2025 release (or later) that falls back to Metaplex's legacy VerifyCollection CPI.";

                var detailedMessage = string.Concat(friendly, "\n\n", upgradeHint);
                return new OwnerGovernedAssetLedgerException(detailedMessage, rawMessage, debugContext: debugContext);
            }

            Debug.LogWarning(
                "[OwnerGovernedAssetLedgerService] Mint guard rail triggered even though the collection update authority already matches the registry mint authority.");
            return new OwnerGovernedAssetLedgerException(friendly, rawMessage, debugContext: debugContext);
        }

        private PublicKey ResolveRegistryConfigForRead()
        {
            if (_registryConfigOverride != null)
            {
                return _registryConfigOverride;
            }

            if (_configNamespace == null)
                throw new InvalidOperationException("Registry namespace is not configured.");

            return DeriveRegistryConfigPda(_configNamespace, null);
        }

        private static void ValidateExpectedPda(string expected, PublicKey actual, string label, string paramName)
        {
            if (string.IsNullOrWhiteSpace(expected))
                return;

            PublicKey expectedKey;
            try
            {
                expectedKey = new PublicKey(expected);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Unable to parse expected {label} PDA.", paramName, ex);
            }

            if (!expectedKey.Equals(actual))
            {
                throw new InvalidOperationException(
                    $"Derived {label} PDA ({actual}) does not match the expected value ({expectedKey}).");
            }
        }

        private PublicKey ResolveNamespace(string requestNamespace)
        {
            if (!string.IsNullOrWhiteSpace(requestNamespace))
            {
                return new PublicKey(requestNamespace);
            }

            if (_configNamespace == null)
                throw new InvalidOperationException("Registry namespace is not configured.");

            return _configNamespace;
        }

        private async Task<OwnerGovernedAssetLedgerConfigAccount> FetchRegistryConfigAsync(string requestNamespace, byte? expectedConfigBump)
        {
            var registryConfigPda = ResolveRegistryConfigAccount(requestNamespace, expectedConfigBump);
            Debug.Log($"[OwnerGovernedAssetLedgerService] ResolveRegistryConfigAccount - { registryConfigPda}");
            return await FetchRegistryConfigAsync(registryConfigPda).ConfigureAwait(false);
        }

        private async Task<OwnerGovernedAssetLedgerConfigAccount> FetchRegistryConfigAsync(PublicKey registryConfigPda)
        {
            Debug.Log("[OwnerGovernedAssetLedgerService] This is the PDA: " + registryConfigPda.ToString());
            var accountInfo = await _rpcClient.GetAccountInfoAsync(registryConfigPda.Key, Commitment.Confirmed).ConfigureAwait(false);
            if (!accountInfo.WasSuccessful || accountInfo.Result?.Value == null)
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the owner-governed asset ledger configuration account. Please verify your Solana configuration.",
                    accountInfo?.Reason);
            }

            var data = accountInfo.Result.Value.Data;
            if (data == null || data.Count == 0 || string.IsNullOrEmpty(data[0]))
            {
                throw new OwnerGovernedAssetLedgerException(
                    "The registry configuration account is missing required data.",
                    "Registry configuration account returned no data.");
            }

            var raw = Convert.FromBase64String(data[0]);
            var owner = accountInfo.Result.Value.Owner;
            if (!string.Equals(owner, _programId.Key, StringComparison.Ordinal))
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the owner-governed asset ledger configuration account. Please verify your Solana configuration.",
                    $"Registry configuration owner mismatch. Expected {_programId.Key}, received {owner ?? "<null>"}.\nAccount: {registryConfigPda}");
            }

            if (raw.Length < ConfigAccountDiscriminator.Length)
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the owner-governed asset ledger configuration account. Please verify your Solana configuration.",
                    $"Registry configuration data too short to verify discriminator. Length {raw.Length}.");
            }

            if (!raw.AsSpan(0, ConfigAccountDiscriminator.Length).SequenceEqual(ConfigAccountDiscriminator))
            {
                throw new OwnerGovernedAssetLedgerException(
                    "Unable to locate the owner-governed asset ledger configuration account. Please verify your Solana configuration.",
                    $"Registry configuration discriminator mismatch. Expected {ToHexString(ConfigAccountDiscriminator)}, received {ToHexString(raw.AsSpan(0, ConfigAccountDiscriminator.Length))}.\nAccount: {registryConfigPda}");
            }

            Debug.Log("[OwnerGovernedAssetLedgerConfigAccount]Deserialize: " + registryConfigPda);
            Debug.Log("[OwnerGovernedAssetLedgerConfigAccount]raw: " + Convert.ToBase64String(raw));

            var configAccount = OwnerGovernedAssetLedgerConfigAccount.Deserialize(raw, registryConfigPda);
            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Registry config deserialized. address='{configAccount.Address}', " +
                $"authority='{configAccount.Authority}', configBump={configAccount.ConfigBump}, authBump={configAccount.AuthBump}, " +
                $"objectCount={configAccount.ObjectCount}, namespace='{configAccount.Namespace}', paused={(configAccount.Paused ? "true" : "false")}.");

            return configAccount;
        }

        private static string ToHexString(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            var result = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                result[i * 2] = HexAlphabet[b >> 4];
                result[i * 2 + 1] = HexAlphabet[b & 0xF];
            }

            return new string(result);
        }

        private static string FormatNullableByte(byte? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "<null>";
        }

        private static string FormatByteArray(byte[] value)
        {
            if (value == null)
                return "<null>";

            if (value.Length == 0)
                return "<empty>";

            return ToHexString(value);
        }

        private static string GetCollectionGuardRailKey(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return null;

            if (message.IndexOf(CollectionAuthorityGuardRailErrorSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                return CollectionAuthorityGuardRailErrorSubstring;

            if (message.IndexOf(CollectionMasterEditionGuardRailErrorSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                return CollectionMasterEditionGuardRailErrorSubstring;

            return null;
        }

        private static bool RequestContainsCreatorVerificationError(RequestResult<string> result)
        {
            if (result == null)
                return false;

            if (ContainsCreatorVerificationError(result.Reason))
                return true;

            if (ContainsCreatorVerificationError(TryGetRequestStringProperty(result, "ServerErrorMessage")))
                return true;

            if (ContainsCreatorVerificationError(TryGetRequestStringProperty(result, "RawRpcResponse")))
                return true;

            var errorData = TryGetRequestProperty(result, "ErrorData");
            if (errorData == null)
                return false;

            if (errorData is string errorDataString)
                return ContainsCreatorVerificationError(errorDataString);

            return ContainsCreatorVerificationError(errorData.ToString());
        }

        private static void LogMintTransactionDebugPayload(string serializedTransaction)
        {
            if (string.IsNullOrWhiteSpace(serializedTransaction))
                return;

            Debug.LogError("[OwnerGovernedAssetLedgerService] Serialized mint transaction (base64) for debugging:");
            Debug.LogError(serializedTransaction);
            Debug.LogError(
                "[OwnerGovernedAssetLedgerService] Reproduce locally with: solana transaction simulate '" +
                serializedTransaction + "' --sig-verify --url <RPC_URL>");
        }

        private static string CombineRawMessages(string primary, string secondary)
        {
            if (string.IsNullOrWhiteSpace(primary))
                return secondary;
            if (string.IsNullOrWhiteSpace(secondary))
                return primary;

            return primary + "\n" + secondary;
        }

        private static string CombineDebugContexts(string primary, string secondary)
        {
            return CombineRawMessages(primary, secondary);
        }

        private static string ExtractRequestReason(RequestResult<string> result)
        {
            if (result == null)
                return null;

            var reason = result.Reason;
            reason = CombineRawMessages(reason, TryGetRequestStringProperty(result, "ServerErrorMessage"));
            reason = CombineRawMessages(reason, TryGetRequestStringProperty(result, "RawRpcResponse"));

            var errorData = TryGetRequestProperty(result, "ErrorData");
            if (errorData != null)
            {
                var errorDataString = errorData as string ?? errorData.ToString();
                reason = CombineRawMessages(reason, errorDataString);
            }

            return reason;
        }

        private static object TryGetRequestProperty(RequestResult<string> result, string propertyName)
        {
            if (result == null || string.IsNullOrWhiteSpace(propertyName))
                return null;

            var property = result.GetType().GetProperty(propertyName);
            return property?.GetValue(result);
        }

        private static string TryGetRequestStringProperty(RequestResult<string> result, string propertyName)
        {
            var value = TryGetRequestProperty(result, propertyName);
            return value as string ?? value?.ToString();
        }

        private static string CreateMasterEditionDebugPayload(CollectionMasterEditionInfo info)
        {
            if (info == null)
                return null;

            var builder = new StringBuilder();
            builder.Append("{\"collectionMasterEdition\":\"");
            builder.Append(info.Address);
            builder.Append("\",\"discriminator\":");
            builder.Append(info.Discriminator.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"supply\":");
            builder.Append(info.Supply.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"hasMaxSupply\":");
            builder.Append(info.HasMaxSupply ? "true" : "false");
            builder.Append(",\"maxSupplyOption\":");
            builder.Append(info.MaxSupplyOption.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"maxSupply\":");
            if (info.MaxSupply.HasValue)
            {
                builder.Append(info.MaxSupply.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append("null");
            }

            builder.Append(",\"isUnique\":");
            builder.Append(info.IsUnique ? "true" : "false");

            builder.Append('}');
            return builder.ToString();
        }

        private sealed class CollectionMasterEditionInfo
        {
            public CollectionMasterEditionInfo(
                PublicKey address,
                byte discriminator,
                ulong supply,
                byte maxSupplyOption,
                ulong? maxSupply)
            {
                Address = address;
                Discriminator = discriminator;
                Supply = supply;
                MaxSupplyOption = maxSupplyOption;
                MaxSupply = maxSupply;
            }

            public PublicKey Address { get; }
            public byte Discriminator { get; }
            public ulong Supply { get; }
            public byte MaxSupplyOption { get; }
            public ulong? MaxSupply { get; }
            public bool HasMaxSupply => MaxSupply.HasValue;
            public bool IsUnique => MaxSupply.HasValue && MaxSupply.Value == 0;
        }

        private readonly struct MasterEditionLayout
        {
            public MasterEditionLayout(byte discriminator, ulong supply, byte maxSupplyOption, ulong? maxSupply)
            {
                Discriminator = discriminator;
                Supply = supply;
                MaxSupplyOption = maxSupplyOption;
                MaxSupply = maxSupply;
            }

            public byte Discriminator { get; }
            public ulong Supply { get; }
            public byte MaxSupplyOption { get; }
            public ulong? MaxSupply { get; }
        }

        private sealed class CollectionMetadataInfo
        {
            public CollectionMetadataInfo(PublicKey updateAuthority, bool? isSizedCollection)
            {
                UpdateAuthority = updateAuthority;
                IsSizedCollection = isSizedCollection;
            }

            public PublicKey UpdateAuthority { get; }
            public bool? IsSizedCollection { get; }
        }

        private static bool TryParseMasterEditionLayout(ReadOnlySpan<byte> raw, out MasterEditionLayout layout)
        {
            layout = default;

            var offset = 0;
            if (!TryReadByte(raw, ref offset, out var discriminator))
                return false;
            if (!TryReadUInt64(raw, ref offset, out var supply))
                return false;
            byte maxSupplyOption;
            ulong? maxSupply = null;

            if (discriminator == MasterEditionV1Discriminator)
            {
                if (!TryReadUInt32(raw, ref offset, out var cOption))
                    return false;
                if (cOption > 1)
                    return false;

                maxSupplyOption = (byte)cOption;
                if (cOption == 1)
                {
                    if (!TryReadUInt64(raw, ref offset, out var parsedMaxSupply))
                        return false;

                    maxSupply = parsedMaxSupply;
                }

                if (!TryAdvance(raw, ref offset, PublicKeyLength * 2))
                    return false;
            }
            else if (discriminator == MasterEditionV2Discriminator)
            {
                if (!TryReadByte(raw, ref offset, out maxSupplyOption))
                    return false;
                if (maxSupplyOption > 1)
                    return false;

                if (maxSupplyOption == 1)
                {
                    if (!TryReadUInt64(raw, ref offset, out var parsedMaxSupply))
                        return false;

                    maxSupply = parsedMaxSupply;
                }
            }
            else
            {
                return false;
            }

            layout = new MasterEditionLayout(discriminator, supply, maxSupplyOption, maxSupply);
            return true;
        }

#if UNITY_EDITOR
        public static bool TryParseMasterEditionLayoutForTests(
            byte[] raw,
            out byte discriminator,
            out ulong supply,
            out byte maxSupplyOption,
            out ulong? maxSupply)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            var success = TryParseMasterEditionLayout(raw.AsSpan(), out var layout);
            if (!success)
            {
                discriminator = default;
                supply = default;
                maxSupplyOption = default;
                maxSupply = null;
                return false;
            }

            discriminator = layout.Discriminator;
            supply = layout.Supply;
            maxSupplyOption = layout.MaxSupplyOption;
            maxSupply = layout.MaxSupply;
            return true;
        }

        public static bool? TryDetectCollectionSizedStateForTests(byte[] raw)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            return TryDetectCollectionSizedState(raw.AsSpan());
        }

        public static bool ShouldEnforceUniqueMasterEditionForTests(bool? isSizedCollection)
        {
            return ShouldEnforceUniqueMasterEdition(isSizedCollection);
        }
#endif

        private static bool? TryDetectCollectionSizedState(ReadOnlySpan<byte> raw)
        {
            try
            {
                var offset = 0;

                if (!TryAdvance(raw, ref offset, 1 + PublicKeyLength + PublicKeyLength))
                    return null;

                if (!TrySkipBorshString(raw, ref offset))
                    return null;
                if (!TrySkipBorshString(raw, ref offset))
                    return null;
                if (!TrySkipBorshString(raw, ref offset))
                    return null;

                if (!TryAdvance(raw, ref offset, sizeof(ushort)))
                    return null;

                if (!TryReadByte(raw, ref offset, out var creatorsFlag))
                    return null;
                if (creatorsFlag > 1)
                    return null;
                if (creatorsFlag == 1)
                {
                    if (!TryReadUInt32(raw, ref offset, out var creatorCount))
                        return null;
                    if (creatorCount > int.MaxValue)
                        return null;

                    for (var i = 0; i < creatorCount; i++)
                    {
                        if (!TryAdvance(raw, ref offset, PublicKeyLength + 1 + 1))
                            return null;
                    }
                }

                if (!TryAdvance(raw, ref offset, 2))
                    return null;

                if (!TryReadByte(raw, ref offset, out var editionFlag))
                    return null;
                if (editionFlag > 1)
                    return null;
                if (editionFlag == 1 && !TryAdvance(raw, ref offset, 1))
                    return null;

                if (!TryReadByte(raw, ref offset, out var tokenStandardMarker))
                    return null;
                if (!TrySkipTokenStandard(raw, ref offset, tokenStandardMarker))
                    return null;

                if (!TryReadByte(raw, ref offset, out var collectionFlag))
                    return null;
                if (collectionFlag > 1)
                    return null;
                if (collectionFlag == 1 && !TryAdvance(raw, ref offset, PublicKeyLength + 1))
                    return null;

                if (!TryReadByte(raw, ref offset, out var usesFlag))
                    return null;
                if (usesFlag > 1)
                    return null;
                if (usesFlag == 1 && !TryAdvance(raw, ref offset, 1 + sizeof(ulong) + sizeof(ulong)))
                    return null;

                if (!TryReadByte(raw, ref offset, out var collectionDetailsFlag))
                    return null;
                if (collectionDetailsFlag == 0)
                    return false;
                if (collectionDetailsFlag != 1)
                    return null;

                if (!TryReadByte(raw, ref offset, out var detailsVariant))
                    return null;
                if (detailsVariant > 1)
                    return null;

                return TryAdvance(raw, ref offset, sizeof(ulong)) ? true : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySkipTokenStandard(ReadOnlySpan<byte> data, ref int offset, byte marker)
        {
            if (marker <= 1)
            {
                if (marker == 0)
                    return true;

                if (!TryReadByte(data, ref offset, out var variant))
                    return false;

                return TrySkipTokenStandardVariant(data, ref offset, variant);
            }

            return TrySkipTokenStandardVariant(data, ref offset, marker);
        }

        private static bool TrySkipTokenStandardVariant(ReadOnlySpan<byte> data, ref int offset, byte variant)
        {
            switch (variant)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    return true;
                case 4:
                case 5:
                    return TrySkipRuleSetToggle(data, ref offset);
                default:
                    return false;
            }
        }

        private static bool TrySkipRuleSetToggle(ReadOnlySpan<byte> data, ref int offset)
        {
            if (!TryReadByte(data, ref offset, out var programmableConfigOption))
                return false;

            switch (programmableConfigOption)
            {
                case 0:
                    return true;
                case 1:
                    if (!TryReadByte(data, ref offset, out var programmableConfigVariant))
                        return false;
                    if (programmableConfigVariant != 0)
                        return false;

                    if (!TryReadByte(data, ref offset, out var ruleSetOption))
                        return false;

                    return ruleSetOption switch
                    {
                        0 => true,
                        1 => TryAdvance(data, ref offset, PublicKeyLength),
                        _ => false,
                    };
                default:
                    return false;
            }
        }

        private static bool TrySkipBorshString(ReadOnlySpan<byte> data, ref int offset)
        {
            if (!TryReadUInt32(data, ref offset, out var length))
                return false;
            if (length > int.MaxValue)
                return false;
            return TryAdvance(data, ref offset, (int)length);
        }

        private static bool TryAdvance(ReadOnlySpan<byte> data, ref int offset, int length)
        {
            if (length < 0)
                return false;

            if (offset > data.Length - length)
                return false;

            offset += length;
            return true;
        }

        private static bool TryReadByte(ReadOnlySpan<byte> data, ref int offset, out byte value)
        {
            if (offset >= data.Length)
            {
                value = 0;
                return false;
            }

            value = data[offset];
            offset += 1;
            return true;
        }

        private static bool TryReadUInt64(ReadOnlySpan<byte> data, ref int offset, out ulong value)
        {
            if (offset > data.Length - sizeof(ulong))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));
            offset += sizeof(ulong);
            return true;
        }

        private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
        {
            if (offset > data.Length - sizeof(uint))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
            offset += sizeof(uint);
            return true;
        }

        private static OwnerGovernedAssetLedgerException CreateMintException(
            string rawMessage,
            Exception innerException = null,
            string debugContext = null)
        {
            var friendly = TryGetAnchorFriendlyMessage(rawMessage);
            if (string.IsNullOrWhiteSpace(friendly))
            {
                friendly = string.IsNullOrWhiteSpace(rawMessage) ? UnknownMintErrorMessage : rawMessage;
            }

            return new OwnerGovernedAssetLedgerException(friendly, rawMessage, innerException, debugContext);
        }

        private static OwnerGovernedAssetLedgerException CreateMigrationException(
            string rawMessage,
            Exception innerException = null,
            string debugContext = null)
        {
            var friendly = TryGetAnchorFriendlyMessage(rawMessage);
            if (string.IsNullOrWhiteSpace(friendly))
            {
                friendly = string.IsNullOrWhiteSpace(rawMessage) ? UnknownMigrationErrorMessage : rawMessage;
            }

            return new OwnerGovernedAssetLedgerException(friendly, rawMessage, innerException, debugContext);
        }

        private static OwnerGovernedAssetLedgerException CreateUpdateException(string rawMessage, Exception innerException = null)
        {
            var friendly = TryGetAnchorFriendlyMessage(rawMessage);
            if (string.IsNullOrWhiteSpace(friendly))
            {
                friendly = string.IsNullOrWhiteSpace(rawMessage) ? UnknownUpdateErrorMessage : rawMessage;
            }

            return new OwnerGovernedAssetLedgerException(friendly, rawMessage, innerException);
        }

        private static OwnerGovernedAssetLedgerException CreateAdministrativeException(
            string fallbackUserMessage,
            string rawMessage,
            Exception innerException = null)
        {
            if (string.IsNullOrWhiteSpace(fallbackUserMessage))
                throw new ArgumentException("Fallback message is required.", nameof(fallbackUserMessage));

            var friendly = TryGetAnchorFriendlyMessage(rawMessage);
            if (string.IsNullOrWhiteSpace(friendly))
            {
                friendly = string.IsNullOrWhiteSpace(rawMessage) ? fallbackUserMessage : rawMessage;
            }

            return new OwnerGovernedAssetLedgerException(friendly, rawMessage, innerException);
        }

        private static List<SignaturePubKeyPair> BuildMintSigners(
            PublicKey payer,
            PublicKey mintAuthority,
            bool mintAuthorityRequiresSignature,
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators,
            IReadOnlyList<PublicKey> permittedSignerPublicKeys)
        {
            var signers = new List<SignaturePubKeyPair>
            {
                new SignaturePubKeyPair
                {
                    PublicKey = payer,
                    Signature = new byte[64]
                }
            };

            var signerKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                payer.Key
            };

            if (!mintAuthority.Equals(payer))
            {
                signerKeys.Add(mintAuthority.Key);

                if (mintAuthorityRequiresSignature)
                {
                    signers.Add(new SignaturePubKeyPair
                    {
                        PublicKey = mintAuthority,
                        Signature = new byte[64]
                    });
                }
            }

            if (permittedSignerPublicKeys != null)
            {
                foreach (var permittedSigner in permittedSignerPublicKeys)
                {
                    if (permittedSigner == null)
                        continue;

                    var permittedKey = permittedSigner.Key;
                    if (string.IsNullOrWhiteSpace(permittedKey))
                        continue;

                    if (!signerKeys.Add(permittedKey))
                        continue;

                    signers.Add(new SignaturePubKeyPair
                    {
                        PublicKey = permittedSigner,
                        Signature = new byte[64]
                    });
                }
            }

            if (creators != null)
            {
                foreach (var creator in creators)
                {
                    if (creator == null || !creator.Verified)
                        continue;

                    var creatorKey = creator.Address?.Key;
                    if (string.IsNullOrEmpty(creatorKey))
                        continue;

                    if (!signerKeys.Add(creatorKey))
                        continue;

                    signers.Add(new SignaturePubKeyPair
                    {
                        PublicKey = creator.Address,
                        Signature = new byte[64]
                    });
                }
            }

            return signers;
        }

        private static string TryGetAnchorFriendlyMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
                return null;

            foreach (var kvp in AnchorErrorFriendlyMessages)
            {
                if (rawMessage.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kvp.Value;
            }

            return null;
        }

        private PublicKey DeriveRegistryConfigPda(PublicKey namespaceKey, byte? expectedBump)
        {
            var seeds = new List<byte[]>
            {
                RegistryConfigSeed,
                namespaceKey.KeyBytes
            };

            if (!PublicKey.TryFindProgramAddress(seeds, _programId, out var pda, out var bump))
                throw new Exception("Unable to derive registry config PDA");

            if (expectedBump.HasValue && expectedBump.Value != bump)
                throw new Exception("Provided config bump does not match derived value.");

            Debug.Log($"[CHECK] Resolved Config PDA: {pda}");

            return pda;
        }

        private PublicKey DeriveMintAuthorityPda(PublicKey registryConfig, byte? expectedBump)
        {
            var seeds = new List<byte[]>
            {
                MintAuthoritySeed,
                registryConfig.KeyBytes
            };

            if (!PublicKey.TryFindProgramAddress(seeds, _programId, out var pda, out var bump))
                throw new Exception("Unable to derive mint authority PDA");

            if (expectedBump.HasValue && expectedBump.Value != bump)
                throw new Exception("Provided auth bump does not match derived value.");

            return pda;
        }

        private PublicKey DeriveManifestPda(PublicKey registryConfig, ulong objectId, byte? expectedBump)
        {
            var objectIdBytes = BitConverter.GetBytes(objectId);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(objectIdBytes);
            }

            var seeds = new List<byte[]>
            {
                ManifestSeed,
                registryConfig.KeyBytes,
                objectIdBytes
            };

            if (!PublicKey.TryFindProgramAddress(seeds, _programId, out var pda, out var bump))
                throw new Exception("Unable to derive manifest PDA");

            if (expectedBump.HasValue && expectedBump.Value != bump)
                throw new Exception("Provided manifest bump does not match derived value.");

            return pda;
        }

        private PublicKey DeriveObjectMintPda(PublicKey manifestPda, byte? expectedBump)
        {
            var seeds = new List<byte[]>
            {
                MintSeed,
                manifestPda.KeyBytes
            };

            if (!PublicKey.TryFindProgramAddress(seeds, _programId, out var pda, out var bump))
                throw new Exception("Unable to derive object mint PDA");

            if (expectedBump.HasValue && expectedBump.Value != bump)
                throw new Exception("Provided mint bump does not match derived value.");

            return pda;
        }

        private static PublicKey DeriveMetadataPda(PublicKey mint)
        {
            var seeds = new List<byte[]>
            {
                Encoding.UTF8.GetBytes("metadata"),
                TokenMetadataProgramId.KeyBytes,
                mint.KeyBytes
            };

            if (!PublicKey.TryFindProgramAddress(seeds, TokenMetadataProgramId, out var pda, out _))
                throw new Exception("Unable to derive metadata PDA");

            return pda;
        }

        private static PublicKey DeriveMasterEditionPda(PublicKey mint)
        {
            var seeds = new List<byte[]>
            {
                Encoding.UTF8.GetBytes("metadata"),
                TokenMetadataProgramId.KeyBytes,
                mint.KeyBytes,
                Encoding.UTF8.GetBytes("edition")
            };

            if (!PublicKey.TryFindProgramAddress(seeds, TokenMetadataProgramId, out var pda, out _))
                throw new Exception("Unable to derive master edition PDA");

            return pda;
        }

        private static Task<T> RunOnUnityThreadAsync<T>(Func<Task<T>> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (!MainThreadDispatcher.Exists())
            {
                return action();
            }

            var completionSource = new TaskCompletionSource<T>();

            try
            {
                MainThreadDispatcher.Instance().Enqueue(() =>
                {
                    try
                    {
                        var task = action();
                        if (task == null)
                        {
                            completionSource.TrySetException(new InvalidOperationException("The provided action returned a null task."));
                            return;
                        }

                        task.ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                var baseException = t.Exception?.GetBaseException() ?? t.Exception;
                                completionSource.TrySetException(baseException ?? new Exception("Sign operation failed."));
                            }
                            else if (t.IsCanceled)
                            {
                                completionSource.TrySetCanceled();
                            }
                            else
                            {
                                completionSource.TrySetResult(t.Result);
                            }
                        }, TaskScheduler.Default);
                    }
                    catch (Exception ex)
                    {
                        completionSource.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }

            return completionSource.Task;
        }

        private static bool ContainsCreatorVerificationError(string message)
        {
            return !string.IsNullOrWhiteSpace(message) &&
                message.IndexOf(CreatorVerificationErrorSubstring, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogCreatorVerificationDiagnostics(
            string message,
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> sanitizedCreators,
            IReadOnlyList<PublicKey> permittedSignerPublicKeys,
            Transaction signedTransaction,
            PublicKey manifestPda,
            PublicKey mintPubKey)
        {
            if (!ContainsCreatorVerificationError(message))
                return;

            var signatureLookup = new Dictionary<string, SignaturePubKeyPair>(StringComparer.Ordinal);
            if (signedTransaction?.Signatures != null)
            {
                foreach (var signature in signedTransaction.Signatures)
                {
                    var signerKey = signature?.PublicKey?.Key;
                    if (string.IsNullOrWhiteSpace(signerKey))
                        continue;

                    signatureLookup[signerKey] = signature;
                }
            }

            var creatorEntries = sanitizedCreators == null
                ? Array.Empty<CreatorVerificationCreatorEntry>()
                : sanitizedCreators
                    .Where(c => c != null)
                    .Select(c =>
                    {
                        var creatorKey = c.Address?.Key;
                        return new CreatorVerificationCreatorEntry
                        {
                            address = creatorKey,
                            verified = c.Verified,
                            share = c.Share,
                            signaturePresent = HasValidSignature(signatureLookup, creatorKey)
                        };
                    })
                    .ToArray();

            var permittedKeys = permittedSignerPublicKeys == null
                ? Array.Empty<string>()
                : permittedSignerPublicKeys
                    .Select(k => k?.Key)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct()
                    .ToArray();

            var signerPresence = permittedKeys
                .Select(k => new CreatorVerificationSignerPresence
                {
                    publicKey = k,
                    signaturePresent = HasValidSignature(signatureLookup, k)
                })
                .ToArray();

            var diagnostics = new CreatorVerificationDiagnostics
            {
                context = message,
                creators = creatorEntries,
                permittedSignerKeys = permittedKeys,
                expectedSignerSignatures = signerPresence,
                manifestPda = manifestPda?.Key,
                mintPda = mintPubKey?.Key
            };

            var payload = JsonUtility.ToJson(diagnostics);
            Debug.LogError($"[OwnerGovernedAssetLedgerService] Creator verification diagnostics: {payload}");
        }

        private static bool HasValidSignature(
            IReadOnlyDictionary<string, SignaturePubKeyPair> signatureLookup,
            string signerKey)
        {
            if (string.IsNullOrWhiteSpace(signerKey) || signatureLookup == null)
                return false;

            if (!signatureLookup.TryGetValue(signerKey, out var signaturePair))
                return false;

            var signature = signaturePair?.Signature;
            return signature != null &&
                signature.Length > 0 &&
                signature.Any(b => b != 0);
        }

        [Serializable]
        private sealed class CreatorVerificationDiagnostics
        {
            public string context;
            public CreatorVerificationCreatorEntry[] creators;
            public string[] permittedSignerKeys;
            public CreatorVerificationSignerPresence[] expectedSignerSignatures;
            public string manifestPda;
            public string mintPda;
        }

        [Serializable]
        private sealed class CreatorVerificationCreatorEntry
        {
            public string address;
            public bool verified;
            public byte share;
            public bool signaturePresent;
        }

        [Serializable]
        private sealed class CreatorVerificationSignerPresence
        {
            public string publicKey;
            public bool signaturePresent;
        }

        private static IReadOnlyList<PublicKey> ResolveMintSignerPublicKeys(
            WalletBase wallet,
            PublicKey payerPublicKey)
        {
            if (wallet == null)
                throw new ArgumentNullException(nameof(wallet));
            if (payerPublicKey == null)
                throw new ArgumentNullException(nameof(payerPublicKey));

            var permittedSigners = new List<PublicKey> { payerPublicKey };

            if (wallet is SessionWallet)
            {
                var externalAuthority = SessionWallet.ExternalAuthorityPublicKey;
                if (externalAuthority != null &&
                    !string.IsNullOrWhiteSpace(externalAuthority.Key) &&
                    !externalAuthority.Equals(payerPublicKey))
                {
                    permittedSigners.Add(externalAuthority);
                }
            }

            return permittedSigners;
        }

        private static IReadOnlyList<OwnerGovernedAssetLedgerCreator> SanitizeMintCreators(
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators,
            PublicKey payerPublicKey,
            IReadOnlyList<PublicKey> permittedSignerPublicKeys)
        {
            if (payerPublicKey == null)
                throw new ArgumentNullException(nameof(payerPublicKey));
            if (permittedSignerPublicKeys == null)
                throw new ArgumentNullException(nameof(permittedSignerPublicKeys));

            var permittedSignerList = new List<string>();
            foreach (var permittedSigner in permittedSignerPublicKeys)
            {
                var key = permittedSigner?.Key;
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!permittedSignerList.Contains(key))
                {
                    permittedSignerList.Add(key);
                }
            }

            if (permittedSignerList.Count == 0)
            {
                permittedSignerList.Add(payerPublicKey.Key);
            }

            var permittedSignerSet = new HashSet<string>(permittedSignerList, StringComparer.Ordinal);

            if (creators == null || creators.Count == 0)
            {
                Debug.Log("[OwnerGovernedAssetLedgerService] No creators provided with the mint request.");
                return Array.Empty<OwnerGovernedAssetLedgerCreator>();
            }

            var sanitized = new List<OwnerGovernedAssetLedgerCreator>(creators.Count);
            var mismatchedCreators = new List<string>();
            int missingAddressCount = 0;

            foreach (var creator in creators)
            {
                if (creator == null)
                    throw new ArgumentException("Creator entries cannot be null.", nameof(creators));

                var address = creator.Address;
                if (creator.Verified)
                {
                    if (address == null || string.IsNullOrWhiteSpace(address.Key))
                    {
                        Debug.LogWarning(
                            $"[OwnerGovernedAssetLedgerService] Creator entry was marked verified but missing an address. Replacing with payer '{payerPublicKey}'.");
                        missingAddressCount++;
                        sanitized.Add(new OwnerGovernedAssetLedgerCreator(payerPublicKey, true, creator.Share));
                        continue;
                    }

                    var addressKey = address.Key;
                    if (!permittedSignerSet.Contains(addressKey))
                    {
                        mismatchedCreators.Add(address.Key);
                        sanitized.Add(new OwnerGovernedAssetLedgerCreator(address, false, creator.Share));
                        continue;
                    }
                }

                if (address == null)
                {
                    Debug.LogWarning(
                        $"[OwnerGovernedAssetLedgerService] Creator entry missing an address. Defaulting to payer '{payerPublicKey}'.");
                    missingAddressCount++;
                    address = payerPublicKey;
                }

                bool verified = creator.Verified && address != null &&
                    permittedSignerSet.Contains(address.Key);
                sanitized.Add(new OwnerGovernedAssetLedgerCreator(address, verified, creator.Share));
            }

            if (mismatchedCreators.Count > 0)
            {
                var permittedSigners = string.Join(
                    ", ",
                    permittedSignerList.Count > 0
                        ? permittedSignerList
                        : new[] { payerPublicKey.Key });
                var mismatchMessage =
                    $"The mint request marks the following creators as verified, but they do not match the connected wallet signers ({permittedSigners}): {string.Join(", ", mismatchedCreators)}. Update the mint request so each verified creator signs the transaction.";
                Debug.LogError($"[OwnerGovernedAssetLedgerService] {mismatchMessage}");
                throw new OwnerGovernedAssetLedgerException(mismatchMessage, mismatchMessage);
            }

            Debug.Log(
                $"[OwnerGovernedAssetLedgerService] Sanitized creator list complete. total={sanitized.Count}, verified={sanitized.Count(c => c.Verified)}, replacedMissingAddresses={missingAddressCount}.");

            return sanitized;
        }

        private static List<string> FindMissingVerifiedCreatorSignatures(
            Transaction signedTransaction,
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators)
        {
            var missingSigners = new List<string>();

            if (creators == null || creators.Count == 0)
                return missingSigners;

            if (signedTransaction == null)
                throw new ArgumentNullException(nameof(signedTransaction));

            var signatureLookup = new Dictionary<string, SignaturePubKeyPair>(StringComparer.Ordinal);

            if (signedTransaction.Signatures != null)
            {
                foreach (var signature in signedTransaction.Signatures)
                {
                    var signerKey = signature?.PublicKey?.Key;
                    if (string.IsNullOrWhiteSpace(signerKey))
                        continue;

                    signatureLookup[signerKey] = signature;
                }
            }

            foreach (var creator in creators)
            {
                if (creator == null || !creator.Verified)
                    continue;

                var creatorKey = creator.Address?.Key;
                if (string.IsNullOrWhiteSpace(creatorKey))
                    continue;

                if (!signatureLookup.TryGetValue(creatorKey, out var signaturePair) ||
                    signaturePair?.Signature == null ||
                    signaturePair.Signature.Length == 0 ||
                    signaturePair.Signature.All(b => b == 0))
                {
                    missingSigners.Add(creatorKey);
                }
            }

            return missingSigners;
        }

        private static List<OwnerGovernedAssetLedgerCreator> DowngradeCreatorsForMissingSignatures(
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators,
            IReadOnlyCollection<string> missingSignerKeys)
        {
            if (creators == null)
                throw new ArgumentNullException(nameof(creators));

            if (missingSignerKeys == null || missingSignerKeys.Count == 0)
                return new List<OwnerGovernedAssetLedgerCreator>(creators);

            var missingLookup = new HashSet<string>(missingSignerKeys, StringComparer.Ordinal);
            var downgraded = new List<OwnerGovernedAssetLedgerCreator>(creators.Count);

            foreach (var creator in creators)
            {
                if (creator == null)
                    throw new ArgumentException("Creator entries cannot be null.", nameof(creators));

                var creatorKey = creator.Address?.Key;
                bool shouldDowngrade =
                    creator.Verified &&
                    !string.IsNullOrWhiteSpace(creatorKey) &&
                    missingLookup.Contains(creatorKey);

                downgraded.Add(shouldDowngrade
                    ? new OwnerGovernedAssetLedgerCreator(creator.Address, false, creator.Share)
                    : creator);
            }

            return downgraded;
        }

        private static List<OwnerGovernedAssetLedgerCreator> DowngradeAllCreatorsToUnverified(
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators,
            out List<string> downgradedAddresses)
        {
            if (creators == null)
            {
                downgradedAddresses = new List<string>();
                return new List<OwnerGovernedAssetLedgerCreator>();
            }

            downgradedAddresses = new List<string>();
            var collectedAddresses = new HashSet<string>(StringComparer.Ordinal);
            var downgraded = new List<OwnerGovernedAssetLedgerCreator>(creators.Count);

            foreach (var creator in creators)
            {
                if (creator == null)
                    throw new ArgumentException("Creator entries cannot be null.", nameof(creators));

                var address = creator.Address?.Key;
                if (creator.Verified && !string.IsNullOrWhiteSpace(address) && collectedAddresses.Add(address))
                {
                    downgradedAddresses.Add(address);
                }

                downgraded.Add(new OwnerGovernedAssetLedgerCreator(creator.Address, false, creator.Share));
            }

            return downgraded;
        }

        private static void MergeDowngradedCreatorAddresses(
            ref List<string> existingAddresses,
            IReadOnlyList<string> additionalAddresses)
        {
            if (additionalAddresses == null || additionalAddresses.Count == 0)
                return;

            if (existingAddresses == null)
            {
                existingAddresses = new List<string>(additionalAddresses);
                return;
            }

            var existingSet = new HashSet<string>(existingAddresses, StringComparer.Ordinal);
            foreach (var address in additionalAddresses)
            {
                if (string.IsNullOrWhiteSpace(address))
                    continue;

                if (existingSet.Add(address))
                {
                    existingAddresses.Add(address);
                }
            }
        }

        private static bool IsTransportError(Exception exception)
        {
            if (exception == null)
            {
                return false;
            }

            if (exception is HttpRequestException || exception is SocketException)
            {
                return true;
            }

            return IsTransportErrorMessage(exception.Message);
        }

        private static bool IsTransportErrorMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("connection refused", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CalculateExponentialBackoffDelay(int attempt, int baseDelayMilliseconds)
        {
            if (attempt <= 0 || baseDelayMilliseconds <= 0)
            {
                return 0;
            }

            int shift = attempt - 1;
            if (shift >= 31)
            {
                return int.MaxValue;
            }

            long delay = (long)baseDelayMilliseconds << shift;
            return delay > int.MaxValue ? int.MaxValue : (int)delay;
        }

        private static MintTransportRetryDecision DetermineTransportRetryDecision(
            int currentAttempt,
            int maxAttempts,
            bool hasSecondaryRpc,
            bool usingSecondaryRpc)
        {
            if (currentAttempt <= maxAttempts)
            {
                return MintTransportRetryDecision.RetryPrimary;
            }

            if (hasSecondaryRpc && !usingSecondaryRpc)
            {
                return MintTransportRetryDecision.FailoverToSecondary;
            }

            return MintTransportRetryDecision.Exhausted;
        }

        private enum MintTransportRetryDecision
        {
            RetryPrimary,
            FailoverToSecondary,
            Exhausted
        }

        private sealed class BlockhashCacheEntry
        {
            public string Value { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private static void AppendCreatorSignerAccounts(
            List<AccountMeta> accounts,
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators)
        {
            if (accounts == null)
                throw new ArgumentNullException(nameof(accounts));

            if (creators == null || creators.Count == 0)
                return;

            var uniqueCreators = new HashSet<string>(StringComparer.Ordinal);
            foreach (var creator in creators)
            {
                if (creator == null || !creator.Verified)
                    continue;

                var addressString = creator.Address?.ToString();
                if (string.IsNullOrWhiteSpace(addressString))
                    continue;

                if (!uniqueCreators.Add(addressString))
                    continue;

                accounts.Add(AccountMeta.ReadOnly(creator.Address, true));
            }
        }

        private static byte[] BuildMigrateConfigNamespaceInstruction(PublicKey newNamespace)
        {
            if (newNamespace == null)
                throw new ArgumentNullException(nameof(newNamespace));

            var buffer = new byte[MigrateConfigNamespaceDiscriminator.Length + newNamespace.KeyBytes.Length];
            Buffer.BlockCopy(MigrateConfigNamespaceDiscriminator, 0, buffer, 0, MigrateConfigNamespaceDiscriminator.Length);
            Buffer.BlockCopy(newNamespace.KeyBytes, 0, buffer, MigrateConfigNamespaceDiscriminator.Length, newNamespace.KeyBytes.Length);
            return buffer;
        }

        private static byte[] BuildMintInstruction(
            OwnerGovernedAssetLedgerMintRequest request,
            IReadOnlyList<OwnerGovernedAssetLedgerCreator> creators)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (creators == null)
                throw new ArgumentNullException(nameof(creators));

            var args = new MintObjectArgs
            {
                ObjectId = request.ObjectId,
                ManifestUri = request.ManifestUri ?? string.Empty,
                ManifestHash = request.ManifestHash,
                MetadataName = request.MetadataName ?? string.Empty,
                MetadataSymbol = request.MetadataSymbol ?? string.Empty,
                SellerFeeBasisPoints = request.SellerFeeBasisPoints,
                Creators = creators
            };

            var serialized = args.Serialize();
            var buffer = new byte[MintObjectDiscriminator.Length + serialized.Length];
            Buffer.BlockCopy(MintObjectDiscriminator, 0, buffer, 0, MintObjectDiscriminator.Length);
            Buffer.BlockCopy(serialized, 0, buffer, MintObjectDiscriminator.Length, serialized.Length);
            return buffer;
        }

        private static byte[] BuildSetAuthorityInstruction(PublicKey newAuthority)
        {
            if (newAuthority == null)
                throw new ArgumentNullException(nameof(newAuthority));

            var buffer = new byte[SetAuthorityDiscriminator.Length + newAuthority.KeyBytes.Length];
            Buffer.BlockCopy(SetAuthorityDiscriminator, 0, buffer, 0, SetAuthorityDiscriminator.Length);
            Buffer.BlockCopy(newAuthority.KeyBytes, 0, buffer, SetAuthorityDiscriminator.Length, newAuthority.KeyBytes.Length);
            return buffer;
        }

        private static byte[] BuildSetPausedInstruction(bool paused)
        {
            var buffer = new byte[SetPausedDiscriminator.Length + 1];
            Buffer.BlockCopy(SetPausedDiscriminator, 0, buffer, 0, SetPausedDiscriminator.Length);
            buffer[SetPausedDiscriminator.Length] = paused ? (byte)1 : (byte)0;
            return buffer;
        }

        private static List<AccountMeta> BuildUpdateManifestAccountList(
            PublicKey owner,
            PublicKey config,
            PublicKey auth,
            PublicKey manifest,
            PublicKey mint,
            PublicKey ownerTokenAccount,
            PublicKey metadata)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (auth == null)
                throw new ArgumentNullException(nameof(auth));
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (mint == null)
                throw new ArgumentNullException(nameof(mint));
            if (ownerTokenAccount == null)
                throw new ArgumentNullException(nameof(ownerTokenAccount));
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            return new List<AccountMeta>
            {
                AccountMeta.Writable(owner, true),
                AccountMeta.Writable(config, false),
                AccountMeta.ReadOnly(auth, false),
                AccountMeta.Writable(manifest, false),
                AccountMeta.ReadOnly(mint, false),
                AccountMeta.ReadOnly(ownerTokenAccount, false),
                AccountMeta.Writable(metadata, false),
                AccountMeta.ReadOnly(TokenMetadataProgramId, false),
                AccountMeta.ReadOnly(RentSysvarId, false),
                AccountMeta.ReadOnly(InstructionsSysvarId, false)
            };
        }

        private static byte[] BuildUpdateInstruction(byte[] manifestHash, string metadataUri, bool isActive)
        {
            if (manifestHash == null)
                throw new ArgumentNullException(nameof(manifestHash));
            if (manifestHash.Length != 32)
                throw new ArgumentException("Manifest hash must be exactly 32 bytes.", nameof(manifestHash));

            var args = new UpdateManifestArgs
            {
                ManifestHash = (byte[])manifestHash.Clone(),
                MetadataUri = metadataUri ?? string.Empty,
                IsActive = isActive
            };

            var serialized = args.Serialize();
            var buffer = new byte[UpdateManifestDiscriminator.Length + serialized.Length];
            Buffer.BlockCopy(UpdateManifestDiscriminator, 0, buffer, 0, UpdateManifestDiscriminator.Length);
            Buffer.BlockCopy(serialized, 0, buffer, UpdateManifestDiscriminator.Length, serialized.Length);
            return buffer;
        }

        private static byte[] CreateAnchorDiscriminator(string name)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(name));
            var result = new byte[8];
            Array.Copy(hash, result, 8);
            return result;
        }

    }
}
