using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// ScriptableObject that stores reusable HTTP upload configuration.
    /// </summary>
    [CreateAssetMenu(fileName = "HttpUploaderProfile", menuName = "Solana Toolbelt/HTTP Uploader Profile")]
    public class HttpUploaderProfile : ScriptableObject
    {
        [Header("Endpoint")]
        [Tooltip("Endpoint that accepts uploads and returns a storage URI.")]
        [SerializeField]
        private string uploadEndpoint = "https://example.com/upload";

        [Tooltip("Timeout in seconds for HTTP POST requests.")]
        [SerializeField]
        private float requestTimeoutSeconds = 15f;

        [Header("Multipart Settings")]
        [Tooltip("Send payloads as multipart/form-data.")]
        [SerializeField]
        private bool useMultipartFormData = true;

        [Tooltip("Field name used for the multipart payload.")]
        [SerializeField]
        private string multipartFieldName = "file";

        [Header("Response Parsing")]
        [Tooltip("Optional JSON path (Newtonsoft syntax) used to extract the URI from the response body.")]
        [SerializeField]
        private string responseUriJsonPath = string.Empty;

        [Header("Authentication")]
        [Tooltip("Header name used for authentication (defaults to Authorization).")]
        [SerializeField]
        private string authenticationHeaderName = "Authorization";

        [Tooltip("Optional authentication header value such as 'Bearer <token>'.")]
        [SerializeField]
        private string authenticationHeaderValue = string.Empty;

        [Tooltip("Additional headers that will be included with each request.")]
        [SerializeField]
        private List<HttpHeaderSetting> additionalHeaders = new();

        [Header("Retry Settings")]
        [Tooltip("Maximum number of retries before surfacing an error (0 disables retries).")]
        [SerializeField]
        private int maxRetries = 2;

        [Tooltip("Initial delay in seconds applied before retrying a failed request.")]
        [SerializeField]
        private float retryBackoffSeconds = 1f;

        [Tooltip("Multiplier applied to the retry delay after each failed attempt.")]
        [SerializeField]
        private float retryBackoffFactor = 2f;

        [Tooltip("When enabled, request timeouts are treated as retryable errors.")]
        [SerializeField]
        private bool retryOnTimeout = true;

        [Tooltip("HTTP status codes that should trigger a retry before failing.")]
        [SerializeField]
        private int[] retryStatusCodes = new[] { 408, 429, 500, 502, 503, 504 };

        /// <summary>
        /// Endpoint used for uploads.
        /// </summary>
        public string UploadEndpoint
        {
            get => uploadEndpoint;
            set => uploadEndpoint = value;
        }

        /// <summary>
        /// Creates a fully-populated set of upload options for the provided payload descriptor.
        /// </summary>
        public HttpUploaderOptions CreateOptions(HttpUploaderPayloadDescriptor payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            if (string.IsNullOrWhiteSpace(uploadEndpoint))
                throw new InvalidOperationException("Upload endpoint is not configured on the profile.");

            var options = new HttpUploaderOptions
            {
                Endpoint = uploadEndpoint,
                Timeout = TimeSpan.FromSeconds(Math.Max(0.0f, requestTimeoutSeconds)),
                UseMultipartFormData = payload.UseMultipartFormData ?? useMultipartFormData,
                MultipartFieldName = string.IsNullOrEmpty(payload.MultipartFieldName) ? multipartFieldName : payload.MultipartFieldName,
                FileName = string.IsNullOrEmpty(payload.FileName) ? "file" : payload.FileName,
                JsonPayload = payload.JsonPayload,
                BinaryPayload = payload.BinaryPayload,
                ContentType = payload.ContentType,
                ResponseUriJsonPath = responseUriJsonPath,
                AuthenticationHeaderName = authenticationHeaderName,
                AuthenticationHeaderValue = string.IsNullOrWhiteSpace(authenticationHeaderValue) ? null : authenticationHeaderValue,
                AdditionalHeaders = BuildAdditionalHeaders(),
                MaxRetries = Math.Max(0, maxRetries),
                RetryBackoffSeconds = Math.Max(0f, retryBackoffSeconds),
                RetryBackoffFactor = Math.Max(1f, retryBackoffFactor),
                RetryOnTimeout = retryOnTimeout,
                RetryStatusCodes = retryStatusCodes?.Length > 0 ? retryStatusCodes.ToArray() : Array.Empty<int>()
            };

            return options;
        }

        private IReadOnlyList<KeyValuePair<string, string>> BuildAdditionalHeaders()
        {
            if (additionalHeaders == null || additionalHeaders.Count == 0)
                return Array.Empty<KeyValuePair<string, string>>();

            return additionalHeaders
                .Where(h => h != null && !string.IsNullOrWhiteSpace(h.Name))
                .Select(h => new KeyValuePair<string, string>(h.Name.Trim(), h.Value ?? string.Empty))
                .ToList();
        }
    }
}
