using UnityEngine;

[CreateAssetMenu(fileName = "NFT_Collection_Config", menuName = "Solana Toolbelt/NFT Collection Config")]
public class NFTCollectionConfig : ScriptableObject
{
    [Tooltip("The existing collection mint address (if any)")]
    public string collectionMint;

    [Tooltip("Metadata URI for this collection (Arweave, etc.)")]
    public string metadataUri;

    [Tooltip("Human‐facing display name (e.g. “My Art Series”)")]
    public string displayName;

    [Tooltip("Symbol (e.g. “ART”)")]
    public string symbol;

    [Tooltip("A PNG or JPG for your NFT")]
    public string avatarUri;

    [Tooltip("Royalty fee, in basis points (e.g. 500 = 5%)")]
    [Range(0, 1000)]
    public ushort sellerFeeBasisPoints;
}