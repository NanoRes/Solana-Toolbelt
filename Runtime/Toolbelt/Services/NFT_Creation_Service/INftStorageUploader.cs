using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Interface for uploading NFT media and metadata to an off-chain storage service.
    /// Implementations can upload binary files (images or videos) and JSON metadata,
    /// returning the URI of the stored content.
    /// </summary>
    public interface INftStorageUploader
    {
        Task<string> UploadMediaAsync(string fileName, byte[] data, string contentType);
        Task<string> UploadJsonAsync(string fileName, string json);
    }
}
