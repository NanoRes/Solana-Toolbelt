using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    public interface INftInventoryService
    {
        Task<IReadOnlyList<TokenAccountSnapshot>> GetTokenAccountsByOwnerAsync(
            string ownerPublicKey,
            string collectionMint = null,
            RpcCommitment commitment = RpcCommitment.Confirmed);

        Task<TokenMintDetails> GetMintDetailsAsync(
            string tokenMint,
            RpcCommitment commitment = RpcCommitment.Confirmed);

        void InvalidateTokenAccountsCache(
            string ownerPublicKey = null,
            string collectionMint = null,
            RpcCommitment? commitment = null);

        void SetTokenAccountCacheTimeToLive(TimeSpan ttl);
    }
}
