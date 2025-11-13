using System;
using System.Collections.Generic;
using System.Net;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Configuration container for an HTTP upload request.
    /// </summary>
    public class HttpUploaderOptions
    {
        private IReadOnlyCollection<int> _retryStatusCodes = new[] { 408, 429, 500, 502, 503, 504 };
        private HashSet<int> _retryStatusSet;

        /// <summary>
        /// Endpoint that will receive the upload.
        /// </summary>
        public string Endpoint { get; set; }

        /// <summary>
        /// Timeout applied to the underlying <see cref="System.Net.Http.HttpClient"/>.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Optional file name to include with multipart uploads.
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Name of the multipart form field when using multipart requests.
        /// </summary>
        public string MultipartFieldName { get; set; } = "file";

        /// <summary>
        /// When true JSON or binary payloads will be wrapped in a multipart/form-data request.
        /// </summary>
        public bool UseMultipartFormData { get; set; } = true;

        /// <summary>
        /// JSON payload to send. Mutually exclusive with <see cref="BinaryPayload"/>.
        /// </summary>
        public string JsonPayload { get; set; }

        /// <summary>
        /// Binary payload to send. Mutually exclusive with <see cref="JsonPayload"/>.
        /// </summary>
        public byte[] BinaryPayload { get; set; }

        /// <summary>
        /// Content type used for the payload. Defaults to application/json for JSON payloads.
        /// </summary>
        public string ContentType { get; set; }

        /// <summary>
        /// Optional JSON path used to extract a URI from the response payload.
        /// </summary>
        public string ResponseUriJsonPath { get; set; }

        /// <summary>
        /// Optional name of the authentication header (defaults to Authorization).
        /// </summary>
        public string AuthenticationHeaderName { get; set; } = "Authorization";

        /// <summary>
        /// Optional value of the authentication header. When empty the header is omitted.
        /// </summary>
        public string AuthenticationHeaderValue { get; set; }

        /// <summary>
        /// Additional headers that should be included with the request.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> AdditionalHeaders { get; set; } = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>
        /// Maximum number of retries to attempt when transient failures occur.
        /// </summary>
        public int MaxRetries { get; set; }

        /// <summary>
        /// Base delay in seconds used for exponential backoff between retries.
        /// </summary>
        public double RetryBackoffSeconds { get; set; } = 1.0;

        /// <summary>
        /// Multiplier used to increase the delay between each retry attempt.
        /// </summary>
        public double RetryBackoffFactor { get; set; } = 2.0;

        /// <summary>
        /// When true, task cancellation caused by an HTTP timeout will be treated as retryable.
        /// </summary>
        public bool RetryOnTimeout { get; set; } = true;

        /// <summary>
        /// HTTP status codes that should trigger a retry.
        /// </summary>
        public IReadOnlyCollection<int> RetryStatusCodes
        {
            get => _retryStatusCodes;
            set
            {
                _retryStatusCodes = value ?? Array.Empty<int>();
                _retryStatusSet = null;
            }
        }

        internal bool ShouldRetryStatus(HttpStatusCode statusCode)
        {
            if (_retryStatusCodes == null || _retryStatusCodes.Count == 0)
                return false;

            _retryStatusSet ??= new HashSet<int>(_retryStatusCodes);
            return _retryStatusSet.Contains((int)statusCode);
        }
    }
}
