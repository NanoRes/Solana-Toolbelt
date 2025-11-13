using System.Threading;
using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Optional contract implemented by uploaders that can estimate the
    /// lamport cost required to store data prior to performing the upload.
    /// </summary>
    public interface IUploadCostEstimator
    {
        /// <summary>
        /// Estimate the lamports required to upload <paramref name="dataSizeBytes"/> bytes.
        /// Returns <c>null</c> when a meaningful estimate cannot be produced.
        /// </summary>
        Task<ulong?> EstimateUploadCostLamportsAsync(int dataSizeBytes, CancellationToken cancellationToken);
    }
}
