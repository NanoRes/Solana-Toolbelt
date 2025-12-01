using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Interface for uploading JSON data to decentralized storage like
    /// Arweave or IPFS. Implementations can use Bundlr, pin services, etc.
    /// </summary>
    public interface ILevelJsonUploader
    {
        /// <summary>
        /// Upload the provided JSON and return the resulting URI.
        /// </summary>
        Task<string> UploadJsonAsync(string fileName, string json);
    }

    /// <summary>
    /// Service that mints player created levels as NFTs.
    /// Handles serialization, upload and mint request construction.
    /// </summary>
    public class LevelEditorMintService<TPlacement, TMetadata>
    {
        private readonly ILevelJsonUploader _uploader;
        private readonly ILevelMetadataSerializer<TPlacement, TMetadata> _serializer;
        private readonly IMintRequestFactory _mintRequestFactory;

        public LevelEditorMintService(
            ILevelJsonUploader uploader,
            ILevelMetadataSerializer<TPlacement, TMetadata> serializer,
            IMintRequestFactory mintRequestFactory)
        {
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _mintRequestFactory = mintRequestFactory ?? throw new ArgumentNullException(nameof(mintRequestFactory));
        }

        /// <summary>
        /// Create the structured DTO describing the level configuration.
        /// </summary>
        public TMetadata CreateLevelMetadata(
            IEnumerable<TPlacement> placements,
            string levelName,
            int tossesAllowed,
            int goalsToWin,
            bool resetAfterToss)
        {
            Debug.Log(
                $"[LevelEditorMintService] Creating metadata. levelName='{levelName}', placements={placements?.Count() ?? 0}, " +
                $"tossesAllowed={tossesAllowed}, goalsToWin={goalsToWin}, resetAfterToss={resetAfterToss}.");
            var metadata = _serializer.CreateLevelMetadata(placements, levelName, tossesAllowed, goalsToWin, resetAfterToss);
            Debug.Log("[LevelEditorMintService] Metadata creation completed.");
            return metadata;
        }

        /// <summary>
        /// Upload the level metadata to Arweave/IPFS via the configured uploader.
        /// </summary>
        public async Task<string> UploadLevelMetadataAsync(TMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            var json = JsonUtility.ToJson(metadata, prettyPrint: false);
            Debug.Log($"[LevelEditorMintService] Uploading metadata JSON of length {json?.Length ?? 0}.");
            string result = await _uploader.UploadJsonAsync($"{Guid.NewGuid()}.json", json);
            Debug.Log($"[LevelEditorMintService] Upload completed. uri='{result}'.");
            return result;
        }

        /// <summary>
        /// Build a mint request compatible with the Owner-Governed Asset Ledger service after the
        /// metadata JSON has been uploaded and hashed.
        /// </summary>
        public OwnerGovernedAssetLedgerMintRequest CreateRegistryMintRequest(
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
            Debug.Log(
                $"[LevelEditorMintService] Creating mint request. objectId={objectId}, manifestUri='{manifestUri}', " +
                $"recipient='{recipientPublicKey}', metadataName='{metadataName}', creators={creators?.Count() ?? 0}.");
            return _mintRequestFactory.CreateMintRequest(
                objectId,
                manifestUri,
                recipientPublicKey,
                manifestHash,
                metadataName,
                metadataSymbol,
                sellerFeeBasisPoints,
                creators,
                configNamespace,
                configBump,
                authBump,
                manifestBump,
                mintBump);
        }
    }
}

