using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Thin adapter that uploads NFT media and metadata using a shared HTTP client profile.
    /// </summary>
    public class HttpNftStorageUploader : MonoBehaviour, INftStorageUploader
    {
        [Header("HTTP Profiles")]
        [Tooltip("Profile used when uploading media files. Falls back to the JSON profile when not assigned.")]
        [SerializeField]
        private HttpUploaderProfile mediaProfile;

        [Tooltip("Profile used when uploading metadata JSON. Falls back to the media profile when not assigned.")]
        [SerializeField]
        private HttpUploaderProfile jsonProfile;

        [Header("Defaults")]
        [Tooltip("Fallback name applied to media uploads when the caller does not provide one.")]
        [SerializeField]
        private string defaultMediaFileName = "media.bin";

        [Tooltip("Fallback name applied to metadata uploads when the caller does not provide one.")]
        [SerializeField]
        private string defaultJsonFileName = "metadata.json";

        private readonly HttpUploaderClient _client = new();

        private HttpUploaderProfile MediaProfile => mediaProfile != null ? mediaProfile : jsonProfile;
        private HttpUploaderProfile JsonProfile => jsonProfile != null ? jsonProfile : mediaProfile;

        /// <inheritdoc />
        public async Task<string> UploadMediaAsync(string fileName, byte[] data, string contentType)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("No data provided", nameof(data));

            var profile = MediaProfile ?? throw new InvalidOperationException("HTTP uploader profile not configured for media uploads.");
            var resolvedFileName = string.IsNullOrEmpty(fileName) ? defaultMediaFileName : fileName;
            var descriptor = HttpUploaderPayloadDescriptor.ForBinary(resolvedFileName, data, contentType);

            try
            {
                return await _client.UploadAsync(
                    profile,
                    descriptor,
                    logWarning: message => Debug.LogWarning($"[HttpNftStorageUploader] {message}"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HttpNftStorageUploader] HTTP error: {ex.Message}");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<string> UploadJsonAsync(string fileName, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON content is empty", nameof(json));

            var profile = JsonProfile ?? throw new InvalidOperationException("HTTP uploader profile not configured for JSON uploads.");
            var resolvedFileName = string.IsNullOrEmpty(fileName) ? defaultJsonFileName : fileName;
            var descriptor = HttpUploaderPayloadDescriptor.ForJson(resolvedFileName, json);

            try
            {
                return await _client.UploadAsync(
                    profile,
                    descriptor,
                    logWarning: message => Debug.LogWarning($"[HttpNftStorageUploader] {message}"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HttpNftStorageUploader] HTTP error: {ex.Message}");
                throw;
            }
        }
    }
}
