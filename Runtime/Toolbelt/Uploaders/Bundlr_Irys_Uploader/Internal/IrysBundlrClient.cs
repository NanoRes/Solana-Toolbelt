using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Solana.Unity.Toolbelt.Internal
{
    internal interface IBundlrSigner
    {
        int SignatureType { get; }
        int SignatureLength { get; }
        int OwnerLength { get; }
        byte[] PublicKey { get; }
        byte[] Sign(byte[] message);
    }

    internal sealed class SolanaBundlrSigner : IBundlrSigner
    {
        private readonly byte[] _privateKey;
        private readonly byte[] _publicKey;

        public int SignatureType => 2; // Ed25519
        public int SignatureLength => 64;
        public int OwnerLength => 32;

        public SolanaBundlrSigner(string privateKeyBase58)
        {
            if (string.IsNullOrWhiteSpace(privateKeyBase58))
                throw new ArgumentException("Private key cannot be null or empty.", nameof(privateKeyBase58));

            var decoded = Base58Utility.Decode(privateKeyBase58.Trim());
            if (decoded.Length == 64)
            {
                _privateKey = new byte[32];
                _publicKey = new byte[32];
                Buffer.BlockCopy(decoded, 0, _privateKey, 0, 32);
                Buffer.BlockCopy(decoded, 32, _publicKey, 0, 32);
            }
            else if (decoded.Length == 32)
            {
                _privateKey = new byte[32];
                Buffer.BlockCopy(decoded, 0, _privateKey, 0, 32);
                _publicKey = new byte[32];
                Ed25519.GeneratePublicKey(_privateKey, 0, _publicKey, 0);
            }
            else
            {
                throw new ArgumentException("Bundlr private key must decode to 32 or 64 bytes.", nameof(privateKeyBase58));
            }
        }

        public byte[] PublicKey
        {
            get
            {
                var copy = new byte[_publicKey.Length];
                Buffer.BlockCopy(_publicKey, 0, copy, 0, _publicKey.Length);
                return copy;
            }
        }

        public byte[] Sign(byte[] message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var signature = new byte[SignatureLength];
            Ed25519.Sign(_privateKey, 0, _publicKey, 0, null, message, 0, message.Length, signature, 0);
            return signature;
        }
    }

    internal sealed class IrysBundlrClient
    {
        private readonly HttpClient _httpClient;
        private readonly IBundlrSigner _signer;
        private readonly string _currency;
        private readonly Func<string, string> _gatewayBuilder;
        private readonly string _addressBase58;

        public IrysBundlrClient(HttpClient httpClient, IBundlrSigner signer, string currency, Func<string, string> gatewayBuilder)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _gatewayBuilder = gatewayBuilder ?? throw new ArgumentNullException(nameof(gatewayBuilder));
            _currency = string.IsNullOrWhiteSpace(currency) ? throw new ArgumentException("Currency is required.", nameof(currency)) : currency;
            _addressBase58 = Base58Utility.Encode(_signer.PublicKey);
        }

        public async Task<BundlrFileUploadResult> UploadAsync(
            byte[] data,
            IReadOnlyList<BundlrTag> tags,
            string fileName,
            string contentType,
            CancellationToken cancellationToken)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Upload data is empty.", nameof(data));

            var dataItem = BundlrDataItem.Create(data, _signer, tags);
            var signaturePayload = dataItem.GetSignatureData();
            var signature = _signer.Sign(signaturePayload);
            dataItem.ApplySignature(signature);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"tx/{_currency}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new ByteArrayContent(dataItem.Binary);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Bundlr upload failed ({(int)response.StatusCode} {response.ReasonPhrase}): {responseBody}");
            }

            var receipt = ParseReceipt(responseBody);
            string transactionId = receipt?.Id ?? ParseUploadId(responseBody) ?? dataItem.GetId();
            string uri = _gatewayBuilder(transactionId);
            return new BundlrFileUploadResult(fileName, contentType, transactionId, uri, receipt);
        }

        public async Task<BigInteger> GetPriceAsync(int dataLength, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync($"price/{_currency}/{dataLength}", cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Bundlr price query failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");

            if (!BigInteger.TryParse(body.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price))
                throw new Exception($"Unable to parse Bundlr price response: {body}");
            return price;
        }

        public async Task<BigInteger> GetBalanceAsync(CancellationToken cancellationToken)
        {
            string uri = $"account/balance/{_currency}?address={_addressBase58}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Bundlr balance query failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");

            try
            {
                var token = JToken.Parse(body);
                var balanceValue = token.Value<string>("balance");
                if (string.IsNullOrEmpty(balanceValue))
                    throw new Exception($"Balance response missing 'balance' property: {body}");

                if (!BigInteger.TryParse(balanceValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var balance))
                    throw new Exception($"Failed to parse Bundlr balance value '{balanceValue}'.");

                return balance;
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonReaderException || ex is ArgumentException)
            {
                throw new Exception($"Unexpected Bundlr balance response: {body}");
            }
        }

        private static BundlrUploadReceipt ParseReceipt(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                var token = JToken.Parse(responseBody);
                if (token.Type != JTokenType.Object)
                    return null;

                var receiptId = token.Value<string>("id");
                var publicKey = token.Value<string>("public");
                var signature = token.Value<string>("signature");
                long? deadlineHeight = TryParseOptionalInt64(token["deadlineHeight"]);
                long? timestamp = TryParseOptionalInt64(token["timestamp"]);
                var version = token.Value<string>("version");

                IReadOnlyList<BundlrValidatorSignature> validatorSignatures = Array.Empty<BundlrValidatorSignature>();
                var validatorToken = token["validatorSignatures"];
                if (validatorToken is JArray validatorArray && validatorArray.Count > 0)
                {
                    var signatures = new List<BundlrValidatorSignature>(validatorArray.Count);
                    foreach (var entry in validatorArray)
                    {
                        if (entry is JObject obj)
                        {
                            var address = obj.Value<string>("address");
                            var validatorSignature = obj.Value<string>("signature");
                            if (!string.IsNullOrEmpty(address) || !string.IsNullOrEmpty(validatorSignature))
                                signatures.Add(new BundlrValidatorSignature(address, validatorSignature));
                        }
                    }

                    if (signatures.Count > 0)
                        validatorSignatures = new ReadOnlyCollection<BundlrValidatorSignature>(signatures);
                }

                if (receiptId == null &&
                    publicKey == null &&
                    signature == null &&
                    deadlineHeight == null &&
                    timestamp == null &&
                    version == null &&
                    validatorSignatures.Count == 0)
                {
                    return null;
                }

                return new BundlrUploadReceipt(
                    receiptId,
                    publicKey,
                    signature,
                    deadlineHeight,
                    timestamp,
                    version,
                    validatorSignatures,
                    responseBody);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ParseUploadId(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                var token = JToken.Parse(responseBody);
                if (token.Type == JTokenType.Object)
                {
                    var id = token.Value<string>("id");
                    if (!string.IsNullOrEmpty(id))
                        return id;

                    var dataNode = token["data"];
                    if (dataNode != null)
                    {
                        var innerId = dataNode.Value<string>("id");
                        if (!string.IsNullOrEmpty(innerId))
                            return innerId;
                    }
                }
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                var trimmed = responseBody.Trim().Trim('"');
                if (!string.IsNullOrEmpty(trimmed))
                    return trimmed;
            }

            return null;
        }

        private static long? TryParseOptionalInt64(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Integer)
                return token.Value<long>();

            if (token.Type == JTokenType.Float)
                return Convert.ToInt64(token.Value<double>());

            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    return value;
            }

            return null;
        }
    }
}
