using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Reusable service that performs HTTP uploads using a shared profile and payload descriptor.
    /// </summary>
    public class HttpUploaderClient
    {
        /// <summary>
        /// Execute an HTTP upload using the provided profile and payload descriptor.
        /// </summary>
        public Task<string> UploadAsync(
            HttpUploaderProfile profile,
            HttpUploaderPayloadDescriptor payload,
            HttpMessageHandler messageHandler = null,
            Action<string> logWarning = null,
            CancellationToken cancellationToken = default)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            var options = profile.CreateOptions(payload);
            return HttpUploaderUtility.UploadAsync(options, messageHandler, logWarning, cancellationToken);
        }
    }
}
