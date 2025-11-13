using Solana.Unity.SDK.Utility;
using Solana.Unity.Rpc;
using System;
using System.IO;
using System.Threading.Tasks;
using Solana.Unity.Metaplex.NFT.Library;
using Solana.Unity.Rpc.Types;
using UnityEngine;
using Solana.Unity.Wallet;
using Solana.Unity.Metaplex.Utilities.Json;

// ReSharper disable once CheckNamespace

namespace Solana.Unity.SDK.Nft
{
    [Serializable]
    public class NftImage : iNftFile<Texture2D>
    {
        public string name { get; set; }
        public string extension { get; set; }
        public string externalUrl { get; set; }
        public Texture2D file { get; set; }
    }

    [Serializable]
    public class Nft
    {
        public Metaplex metaplexData;

        public Nft() { }

        public Nft(Metaplex metaplexData)
        {
            this.metaplexData = metaplexData;
        }

        /// <summary>
        /// Returns all data for listed nft
        /// </summary>
        /// <param name="mint"></param>
        /// <param name="connection">Rpc client</param>
        ///         /// <param name="loadTexture"></param>

        /// <param name="imageHeightAndWidth"></param>
        /// <param name="tryUseLocalContent">If use local content for image</param>
        /// <param name="commitment"></param>
        /// <returns></returns>
        public static async Task<Nft> TryGetNftData(
            string mint,
            IRpcClient connection, 
            bool loadTexture = true,
            int imageHeightAndWidth = 256,
            bool tryUseLocalContent = true,
            Commitment commitment = Commitment.Confirmed)
        {
            if (tryUseLocalContent)
            { 
                var nft = TryLoadNftFromLocal(mint);
                if(nft != null && loadTexture) await nft.LoadTexture();
                if (nft != null) return nft;
            }
            var newData = await MetadataAccount.GetAccount( connection, new PublicKey(mint), commitment);
            
            if (newData?.metadata == null || newData?.offchainData == null) return null;

            var met = new Metaplex(newData);
            var newNft = new Nft(met);

            if (loadTexture) await newNft.LoadTexture(imageHeightAndWidth);
            
            FileLoader.SaveToPersistentDataPath(Path.Combine(Application.persistentDataPath, $"{mint}.json"), newNft.metaplexData.data);
            return newNft;
        }

        /// <summary>
        /// Returns Nft from local machine if it exists
        /// </summary>
        /// <param name="mint"></param>
        /// <returns></returns>
        public static Nft TryLoadNftFromLocal(string mint)
        {
            var metadataAccount = FileLoader.LoadFileFromLocalPath<MetadataAccount>($"{Path.Combine(Application.persistentDataPath, mint)}.json");
            if (metadataAccount == null) return null;
            
            var local = new Nft(new Metaplex(metadataAccount));
            
            var tex = FileLoader.LoadFileFromLocalPath<Texture2D>($"{Path.Combine(Application.persistentDataPath, mint)}.png");
            if (tex)
            {
                local.metaplexData.nftImage = new NftImage();
                local.metaplexData.nftImage.file = tex;
            }
            else
            {
                return null;
            }

            return local;
        }
        
        /// <summary>
        /// Load the texture of the NFT
        /// </summary>
        /// <param name="imageHeightAndWidth"></param>
        public async Task LoadTexture(int imageHeightAndWidth = 256)
        {
            var offchainData = metaplexData?.data?.offchainData;
            if (offchainData == null) return;
            if (metaplexData.nftImage != null) return;
            var nftImage = new NftImage();
            var imageUrl = ResolveImageUrl(offchainData);

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                Debug.LogWarning($"No image URL found for {metaplexData?.data?.mint}. Texture load skipped.");
                return;
            }

            var texture = await FileLoader.LoadFile<Texture2D>(imageUrl);
            if (texture == null)
            {
                Debug.LogWarning($"Unable to load NFT texture from {imageUrl}");
                return;
            }
            var compressedTexture = FileLoader.Resize(texture, imageHeightAndWidth, imageHeightAndWidth);
            if (compressedTexture)
            {
                nftImage.externalUrl = imageUrl;
                nftImage.file = compressedTexture;
                metaplexData.nftImage = nftImage;
            }
            FileLoader.SaveToPersistentDataPath(Path.Combine(Application.persistentDataPath, $"{metaplexData.data.mint}.png"), compressedTexture);
        }

        private static string ResolveImageUrl(MetaplexTokenStandard offchainData)
        {
            if (offchainData == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(offchainData.default_image))
            {
                return offchainData.default_image;
            }

            var image = ResolveOptionalMetadataField(offchainData, "image");
            if (!string.IsNullOrWhiteSpace(image))
            {
                return image;
            }

            var previewUrl = ResolveOptionalMetadataField(offchainData, "previewUrl")
                               ?? ResolveOptionalMetadataField(offchainData, "preview_url");
            if (!string.IsNullOrWhiteSpace(previewUrl))
            {
                return previewUrl;
            }

            if (!string.IsNullOrWhiteSpace(offchainData.animation_url))
            {
                return offchainData.animation_url;
            }

            if (!string.IsNullOrWhiteSpace(offchainData.external_url))
            {
                return offchainData.external_url;
            }

            var files = offchainData.properties?.files;
            if (files != null)
            {
                foreach (var file in files)
                {
                    if (!string.IsNullOrWhiteSpace(file?.uri))
                    {
                        return file.uri;
                    }
                }
            }

            return null;
        }

        private static string ResolveOptionalMetadataField(MetaplexTokenStandard offchainData, string memberName)
        {
            if (offchainData == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var type = offchainData.GetType();
            var property = type.GetProperty(memberName);
            if (property != null && property.PropertyType == typeof(string))
            {
                return property.GetValue(offchainData) as string;
            }

            var field = type.GetField(memberName);
            if (field != null && field.FieldType == typeof(string))
            {
                return field.GetValue(offchainData) as string;
            }

            return null;
        }
    }
}
