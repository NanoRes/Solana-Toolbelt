using UnityEngine;

/// <summary>
/// ScriptableObject that stores Solana chain addresses used by the game.
/// Keeping these in a separate asset makes it easy to swap between
/// devnet, testnet or mainnet configurations.
/// </summary>
[CreateAssetMenu(fileName = "Chain_Addresses", menuName = "Solana Toolbelt/Chain Addresses")]
public class ChainAddressesSO : ScriptableObject
{
    [Tooltip("Program ID of the deployed Owner-Governed Asset Ledger program")]
    public string ownerGovernedAssetLedgerProgramId;

    [Tooltip("Mint address for the Levels collection NFT")]
    public string levelsCollectionMint;

    [Tooltip("Namespace public key used to derive the registry configuration PDA. Leave empty if you provide explicit PDA addresses.")]
    public string ownerGovernedAssetLedgerNamespace;

    [Tooltip("Optional override for the registry config PDA. Supply when you want to skip namespace derivation.")]
    public string ownerGovernedAssetLedgerConfigAccount;

    [Tooltip("Optional override for the registry mint authority PDA. Supply when you want to skip namespace derivation.")]
    public string ownerGovernedAssetLedgerAuthorityAccount;

    [Tooltip("HTTP RPC endpoint")]
    public string rpcUrl;

    [Tooltip("WebSocket endpoint for subscriptions")]
    public string wsUrl;
}
