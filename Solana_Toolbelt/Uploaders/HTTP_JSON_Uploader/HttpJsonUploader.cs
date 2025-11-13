using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Component that adapts a <see cref="HttpUploaderProfile"/> into the
    /// <see cref="ILevelJsonUploader"/> interface used by the mint services.
    /// </summary>
    public class HttpJsonUploader : MonoBehaviour, ILevelJsonUploader
    {
        [Header("HTTP Profile")]
        [Tooltip("Profile that defines endpoint, headers and retry behaviour for JSON uploads.")]
        [SerializeField]
        private HttpUploaderProfile profile;

        [Header("Upload Settings")]
        [Tooltip("Fallback file name used when one is not provided by the caller.")]
        [SerializeField]
        private string defaultFileName = "file.json";

        private readonly HttpUploaderClient _client = new();

        /// <summary>
        /// Profile used for uploads. Can be swapped at runtime.
        /// </summary>
        public HttpUploaderProfile Profile
        {
            get => profile;
            set => profile = value;
        }

        /// <inheritdoc />
        public async Task<string> UploadJsonAsync(string fileName, string json)
        {
            if (profile == null)
                throw new InvalidOperationException("HTTP uploader profile not configured.");

            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON content is empty", nameof(json));

            var resolvedFileName = string.IsNullOrEmpty(fileName) ? defaultFileName : fileName;
            var descriptor = HttpUploaderPayloadDescriptor.ForJson(resolvedFileName, json);

            Debug.Log(
                $"[HttpJsonUploader] UploadJsonAsync started. resolvedFileName='{resolvedFileName}', jsonLength={json?.Length ?? 0}.");

            try
            {
                string result = await _client.UploadAsync(
                    profile,
                    descriptor,
                    logWarning: message => Debug.LogWarning($"[HttpJsonUploader] {message}"));
                Debug.Log($"[HttpJsonUploader] UploadJsonAsync completed. uri='{result}'.");
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HttpJsonUploader] HTTP error: {ex.Message}");
                throw;
            }
        }
    }
}
