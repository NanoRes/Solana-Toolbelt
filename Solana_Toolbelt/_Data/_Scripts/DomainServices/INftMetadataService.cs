using System.Threading.Tasks;
using System.Collections.Generic;

namespace Solana.Unity.Toolbelt
{
    public interface INftMetadataService
    {
        Task<NftUriResult> GetOnChainUriAsync(
            string mintAddress,
            RpcCommitment commitment = RpcCommitment.Processed);

        Task<string> FetchMetadataJsonAsync(string uri);

        Task<MetadataAccountResult> GetMetadataAccountAsync(
            string mintAddress,
            RpcCommitment commitment = RpcCommitment.Processed);

        Task<IReadOnlyDictionary<string, MetadataAccountResult>> GetMetadataAccountsAsync(
            IEnumerable<string> mintAddresses,
            RpcCommitment commitment = RpcCommitment.Processed);
    }
}
