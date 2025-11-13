using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Builds mint requests with the appropriate metadata required by the Owner-Governed Asset Ledger.
    /// </summary>
    public interface IMintRequestFactory
    {
        OwnerGovernedAssetLedgerMintRequest CreateMintRequest(
            ulong objectId,
            string manifestUri,
            string recipientPublicKey,
            byte[] manifestHash,
            string metadataName,
            string metadataSymbol,
            ushort sellerFeeBasisPoints,
            IEnumerable<OwnerGovernedAssetLedgerCreator> creators,
            string configNamespace = null,
            byte? configBump = null,
            byte? authBump = null,
            byte? manifestBump = null,
            byte? mintBump = null);
    }

    /// <summary>
    /// Default mint request factory implementation.
    /// </summary>
    public sealed class DefaultMintRequestFactory : IMintRequestFactory
    {
        public OwnerGovernedAssetLedgerMintRequest CreateMintRequest(
            ulong objectId,
            string manifestUri,
            string recipientPublicKey,
            byte[] manifestHash,
            string metadataName,
            string metadataSymbol,
            ushort sellerFeeBasisPoints,
            IEnumerable<OwnerGovernedAssetLedgerCreator> creators,
            string configNamespace = null,
            byte? configBump = null,
            byte? authBump = null,
            byte? manifestBump = null,
            byte? mintBump = null)
        {
            if (string.IsNullOrWhiteSpace(manifestUri))
                throw new ArgumentException("Manifest URI is required.", nameof(manifestUri));
            if (string.IsNullOrWhiteSpace(recipientPublicKey))
                throw new ArgumentException("Recipient public key is required.", nameof(recipientPublicKey));
            if (manifestHash == null)
                throw new ArgumentNullException(nameof(manifestHash));
            if (manifestHash.Length != 32)
                throw new ArgumentException("Manifest hash must be exactly 32 bytes.", nameof(manifestHash));

            if (creators == null)
                throw new ArgumentNullException(nameof(creators));

            var signerPublicKeys = MintSignerUtility.GatherAvailableSignerPublicKeys(ToolbeltRuntime.Services);
            var sanitizedCreators = MintSignerUtility.EnforceSignerVerification(creators, signerPublicKeys);

            Debug.Log(
                $"[MintRequestFactory] Building request. objectId={objectId}, manifestUri='{manifestUri}', recipient='{recipientPublicKey}', " +
                $"metadataName='{metadataName}', creators={sanitizedCreators.Count}.");

            return new OwnerGovernedAssetLedgerMintRequest(
                objectId,
                manifestUri,
                recipientPublicKey,
                manifestHash,
                metadataName,
                metadataSymbol,
                sellerFeeBasisPoints,
                sanitizedCreators,
                configNamespace,
                configBump,
                authBump,
                manifestBump,
                mintBump);
        }
    }
}
