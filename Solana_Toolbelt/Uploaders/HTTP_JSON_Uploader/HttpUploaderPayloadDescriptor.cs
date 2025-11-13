using System;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Describes a payload that should be uploaded using <see cref="HttpUploaderClient"/>.
    /// </summary>
    public class HttpUploaderPayloadDescriptor
    {
        /// <summary>
        /// Optional file name associated with the payload.
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// JSON payload to upload. Mutually exclusive with <see cref="BinaryPayload"/>.
        /// </summary>
        public string JsonPayload { get; set; }

        /// <summary>
        /// Binary payload to upload. Mutually exclusive with <see cref="JsonPayload"/>.
        /// </summary>
        public byte[] BinaryPayload { get; set; }

        /// <summary>
        /// Optional content type to include with the payload.
        /// </summary>
        public string ContentType { get; set; }

        /// <summary>
        /// Overrides the profile's multipart behaviour when specified.
        /// </summary>
        public bool? UseMultipartFormData { get; set; }

        /// <summary>
        /// Overrides the profile's multipart field name when provided.
        /// </summary>
        public string MultipartFieldName { get; set; }

        /// <summary>
        /// Helper for constructing a descriptor that uploads JSON content.
        /// </summary>
        public static HttpUploaderPayloadDescriptor ForJson(
            string fileName,
            string json,
            bool? useMultipart = null,
            string multipartFieldName = null,
            string contentType = "application/json")
        {
            if (json == null)
                throw new ArgumentNullException(nameof(json));

            return new HttpUploaderPayloadDescriptor
            {
                FileName = fileName,
                JsonPayload = json,
                ContentType = string.IsNullOrEmpty(contentType) ? "application/json" : contentType,
                UseMultipartFormData = useMultipart,
                MultipartFieldName = multipartFieldName
            };
        }

        /// <summary>
        /// Helper for constructing a descriptor that uploads binary data.
        /// </summary>
        public static HttpUploaderPayloadDescriptor ForBinary(
            string fileName,
            byte[] data,
            string contentType = null,
            bool? useMultipart = null,
            string multipartFieldName = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            return new HttpUploaderPayloadDescriptor
            {
                FileName = fileName,
                BinaryPayload = data,
                ContentType = contentType,
                UseMultipartFormData = useMultipart,
                MultipartFieldName = multipartFieldName
            };
        }
    }
}
