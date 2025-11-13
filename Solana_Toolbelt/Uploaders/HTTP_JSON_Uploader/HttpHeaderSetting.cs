using System;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Serializable configuration for an HTTP header. Allows specifying additional
    /// headers from the Unity inspector.
    /// </summary>
    [Serializable]
    public class HttpHeaderSetting
    {
        [Tooltip("Header name, e.g. 'Authorization' or 'x-api-key'.")]
        public string Name;

        [Tooltip("Header value that will be sent with the request.")]
        public string Value;
    }
}
