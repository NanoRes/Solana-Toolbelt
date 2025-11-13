using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solana.Unity.Metaplex.NFT.Library;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Service that mints user-generated NFTs without attaching them to a master collection.
    /// Media (image or video) is uploaded off-chain and referenced in the metadata.
    /// </summary>
    public class UserGeneratedNftMintService
    {
        private readonly SolanaNFTMintService _mintService;
        private readonly INftStorageUploader _uploader;

        public UserGeneratedNftMintService(SolanaNFTMintService mintService, INftStorageUploader uploader)
        {
            _mintService = mintService ?? throw new ArgumentNullException(nameof(mintService));
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        }

        /// <summary>
        /// Upload the provided media and metadata then mint the NFT.
        /// </summary>
        /// <param name="mediaData">Raw bytes of the image or video.</param>
        /// <param name="mediaFileName">File name including extension.</param>
        /// <param name="mediaContentType">Content type such as "image/png" or "video/mp4".</param>
        /// <param name="name">Name of the NFT.</param>
        /// <param name="symbol">Symbol for the NFT.</param>
        /// <param name="description">Description text.</param>
        /// <param name="isVideo">True if the media represents a video.</param>
        /// <param name="sellerFeeBasisPoints">Royalty percentage in basis points.</param>
        /// <param name="creatorAddress">Optional creator wallet address for metadata.</param>
        /// <param name="memo">Optional memo attached to the mint transaction.</param>
        public async Task<MintResult> MintUserGeneratedNftAsync(
            byte[] mediaData,
            string mediaFileName,
            string mediaContentType,
            string name,
            string symbol,
            string description,
            bool isVideo,
            ushort sellerFeeBasisPoints,
            string creatorAddress,
            string memo = null)
        {
            if (mediaData == null || mediaData.Length == 0)
                throw new ArgumentException("No media data provided", nameof(mediaData));

            string mediaUri = await _uploader.UploadMediaAsync(mediaFileName, mediaData, mediaContentType);

            var meta = new NftMetadata
            {
                name = name,
                symbol = symbol,
                description = description,
                image = isVideo ? null : mediaUri,
                attributes = new List<Attribute>(),
                additionalFields = isVideo ? new Dictionary<string, object> { { "animation_url", mediaUri } } : null
            };

            string metadataJson = CompactJsonSerializer.Serialize(meta);
            string metadataUri = await _uploader.UploadJsonAsync($"{Guid.NewGuid()}.json", metadataJson);

            var creators = new List<Creator>();
            if (!string.IsNullOrEmpty(creatorAddress))
            {
                creators.Add(new Creator(new Solana.Unity.Wallet.PublicKey(creatorAddress), 100, true));
            }

            var request = new MintRequest
            {
                CollectionMint = null, // intentionally not part of a master collection
                MetadataUri = metadataUri,
                Name = name,
                Symbol = symbol,
                SellerFeeBasisPoints = sellerFeeBasisPoints,
                Creators = creators,
                IsMutable = true,
                Quantity = 1
            };

            return await _mintService.MintAndVerifyAsync(request, memo);
        }
    }
}
