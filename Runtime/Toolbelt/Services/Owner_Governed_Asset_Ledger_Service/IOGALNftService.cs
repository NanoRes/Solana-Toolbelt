using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Interface for interacting with the on-chain Owner-Governed Asset Ledger program.
    /// Implementations are expected to mint new object NFTs, update an
    /// existing object's manifest and fetch the current manifest data.
    /// </summary>
    public interface IOGALNftService
    {
        /// <summary>
        /// Mint a new object NFT under the configured collection and return
        /// the resulting mint address and transaction signature.
        /// </summary>
        Task<OwnerGovernedAssetLedgerMintResult> MintObjectNftAsync(OwnerGovernedAssetLedgerMintRequest request);

        /// <summary>
        /// Update the manifest of an existing object NFT. Only the current
        /// holder of the NFT should be able to perform this action.
        /// </summary>
        Task<string> UpdateManifestAsync(
            ulong objectId,
            string objectMintAddress,
            string ownerTokenAccountAddress,
            byte[] manifestHash,
            string metadataUri,
            bool isActive);

        /// <summary>
        /// Fetch the manifest PDA associated with an object NFT.
        /// Returns the on-chain manifest data structure.
        /// </summary>
        Task<ObjectManifestAccount> GetManifestAsync(ulong objectId);

        /// <summary>
        /// Updates the registry configuration authority to the provided public key.
        /// </summary>
        /// <param name="newAuthorityPublicKey">Public key that should become the new config authority.</param>
        /// <param name="requestNamespace">
        /// Optional namespace override used to derive the config PDA when this service was
        /// created without a baked-in namespace.
        /// </param>
        /// <param name="expectedConfigBump">
        /// Optional bump used to sanity check the derived configuration PDA.
        /// </param>
        Task<string> SetAuthorityAsync(
            string newAuthorityPublicKey,
            string requestNamespace = null,
            byte? expectedConfigBump = null);

        /// <summary>
        /// Toggles the paused flag on the registry configuration account.
        /// </summary>
        /// <param name="paused">True to pause minting, false to resume.</param>
        /// <param name="requestNamespace">
        /// Optional namespace override used to derive the config PDA when this service was
        /// created without a baked-in namespace.
        /// </param>
        /// <param name="expectedConfigBump">
        /// Optional bump used to sanity check the derived configuration PDA.
        /// </param>
        Task<string> SetPausedAsync(
            bool paused,
            string requestNamespace = null,
            byte? expectedConfigBump = null);
    }
}
