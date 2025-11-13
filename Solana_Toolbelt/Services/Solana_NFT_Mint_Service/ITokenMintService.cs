using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Defines a service that can mint one or more editions of a given NFT collection.
    /// Each NFT is minted in its own transaction and the signatures are returned.
    /// </summary>
    public interface ITokenMintService
    {
        /// <summary>
        /// Mint <c>request.Quantity</c> copies of a given NFT (Metadata + MasterEdition).
        /// A single transaction is created per copy, returning each signature in order.
        /// </summary>
        /// <param name="request">All the required on‐chain parameters.</param>
        /// <returns>A MintResult indicating success, any error, and the transaction signatures.</returns>
        Task<MintResult> MintAndVerifyAsync(MintRequest request, string memo = null);
    }
}