using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Helper responsible for constructing requests, handling retries and parsing
    /// responses for the HTTP uploaders.
    /// </summary>
    public static class HttpUploaderUtility
    {
        /// <summary>
        /// Execute an HTTP request with retry/backoff behaviour and return the resulting URI.
        /// </summary>
        public static async Task<string> UploadAsync(
            HttpUploaderOptions options,
            HttpMessageHandler messageHandler = null,
            Action<string> logWarning = null,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Endpoint))
                throw new ArgumentException("An upload endpoint must be provided.", nameof(options));
            if (!string.IsNullOrEmpty(options.JsonPayload) && options.BinaryPayload != null)
                throw new ArgumentException("Only a JSON or binary payload can be provided, not both.");
            if (string.IsNullOrEmpty(options.JsonPayload) && (options.BinaryPayload == null || options.BinaryPayload.Length == 0))
                throw new ArgumentException("A JSON or binary payload is required.");

            var httpClient = messageHandler != null
                ? new HttpClient(messageHandler, disposeHandler: false)
                : new HttpClient();

            httpClient.Timeout = options.Timeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(15)
                : options.Timeout;

            try
            {
                using var response = await SendWithRetriesAsync(httpClient, options, logWarning, cancellationToken)
                    .ConfigureAwait(false);

                var responseBody = response.Content != null
                    ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                    : string.Empty;

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
                }

                return ExtractUri(responseBody, options.ResponseUriJsonPath);
            }
            finally
            {
                httpClient.Dispose();
            }
        }

        private static async Task<HttpResponseMessage> SendWithRetriesAsync(
            HttpClient httpClient,
            HttpUploaderOptions options,
            Action<string> logWarning,
            CancellationToken cancellationToken)
        {
            Exception lastException = null;
            var maxRetries = Math.Max(0, options.MaxRetries);

            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var request = BuildRequest(options);
                HttpResponseMessage response = null;

                try
                {
                    response = await httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    var shouldRetry = options.ShouldRetryStatus(response.StatusCode);
                    var responseText = response.Content != null
                        ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                        : string.Empty;

                    if (!shouldRetry || attempt == maxRetries)
                    {
                        throw new HttpRequestException(
                            $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseText}");
                    }

                    lastException = new HttpRequestException(
                        $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseText}");

                    logWarning?.Invoke(
                        $"Upload attempt {(attempt + 1)} failed with status {(int)response.StatusCode}. Retrying...");
                }
                catch (TaskCanceledException ex)
                    when (!cancellationToken.IsCancellationRequested && options.RetryOnTimeout && attempt < maxRetries)
                {
                    lastException = ex;
                    logWarning?.Invoke($"Upload attempt {(attempt + 1)} timed out. Retrying...");
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    lastException = ex;
                    logWarning?.Invoke($"Upload attempt {(attempt + 1)} failed: {ex.Message}. Retrying...");
                }
                finally
                {
                    response?.Dispose();
                }

                if (attempt < maxRetries)
                {
                    var delaySeconds = Math.Max(0.0, options.RetryBackoffSeconds) * Math.Pow(
                        Math.Max(1.0, options.RetryBackoffFactor),
                        attempt);

                    if (delaySeconds > 0)
                    {
                        var delay = TimeSpan.FromSeconds(delaySeconds);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            throw lastException ?? new HttpRequestException("Request failed after retries.");
        }

        private static HttpRequestMessage BuildRequest(HttpUploaderOptions options)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = BuildContent(options)
            };

            if (!string.IsNullOrWhiteSpace(options.AuthenticationHeaderValue))
            {
                var headerName = string.IsNullOrWhiteSpace(options.AuthenticationHeaderName)
                    ? "Authorization"
                    : options.AuthenticationHeaderName;

                request.Headers.TryAddWithoutValidation(headerName, options.AuthenticationHeaderValue);
            }

            if (options.AdditionalHeaders != null)
            {
                foreach (var header in options.AdditionalHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header.Key))
                        continue;

                    request.Headers.TryAddWithoutValidation(header.Key, header.Value ?? string.Empty);
                }
            }

            return request;
        }

        private static HttpContent BuildContent(HttpUploaderOptions options)
        {
            if (options.UseMultipartFormData)
            {
                var multipart = new MultipartFormDataContent();
                multipart.Add(CreatePayloadContent(options),
                    string.IsNullOrEmpty(options.MultipartFieldName) ? "file" : options.MultipartFieldName,
                    string.IsNullOrEmpty(options.FileName) ? "file" : options.FileName);
                return multipart;
            }

            return CreatePayloadContent(options);
        }

        private static HttpContent CreatePayloadContent(HttpUploaderOptions options)
        {
            if (!string.IsNullOrEmpty(options.JsonPayload))
            {
                var mediaType = string.IsNullOrEmpty(options.ContentType) ? "application/json" : options.ContentType;
                return new StringContent(options.JsonPayload, System.Text.Encoding.UTF8, mediaType);
            }

            if (options.BinaryPayload != null)
            {
                var content = new ByteArrayContent(options.BinaryPayload);
                if (!string.IsNullOrEmpty(options.ContentType))
                {
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);
                }

                return content;
            }

            throw new InvalidOperationException("An upload payload must be provided.");
        }

        private static string ExtractUri(string responseBody, string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath))
                return responseBody?.Trim();

            if (string.IsNullOrWhiteSpace(responseBody))
                throw new InvalidOperationException(
                    "Expected a JSON response body but the server returned an empty payload.");

            try
            {
                var token = JToken.Parse(responseBody);
                var valueToken = token.SelectToken(jsonPath);
                if (valueToken == null || valueToken.Type == JTokenType.Null)
                {
                    throw new InvalidOperationException(
                        $"JSON path '{jsonPath}' was not found in the response body.");
                }

                return valueToken.Type == JTokenType.String
                    ? valueToken.Value<string>()?.Trim()
                    : valueToken.ToString(Formatting.None);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Failed to parse JSON response: " + ex.Message, ex);
            }
        }
    }
}
