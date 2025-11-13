using System.Threading;
using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Abstraction over the storage primitives used by the Solana Toolbelt.
    /// Provides asynchronous access to JSON blobs stored on disk and
    /// lightweight key-value flags used to track feature state.
    /// </summary>
    public interface ISolanaStorageService
    {
        /// <summary>
        /// Read a JSON document previously stored via <see cref="WriteJsonAsync"/>.
        /// </summary>
        /// <param name="identifier">Logical identifier or relative file name.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <typeparam name="T">The strongly typed payload to deserialize.</typeparam>
        Task<T> ReadJsonAsync<T>(string identifier, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persist a JSON document for later retrieval.
        /// </summary>
        /// <param name="identifier">Logical identifier or relative file name.</param>
        /// <param name="payload">The object graph to persist.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task WriteJsonAsync<T>(string identifier, T payload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete a previously stored JSON document.
        /// </summary>
        /// <param name="identifier">Logical identifier or relative file name.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task DeleteAsync(string identifier, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieve a boolean flag persisted in the lightweight key-value store.
        /// </summary>
        /// <param name="key">Key associated with the stored flag.</param>
        /// <param name="defaultValue">Value returned when the key is missing.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task<bool> GetFlagAsync(string key, bool defaultValue = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persist or update a boolean flag in the lightweight key-value store.
        /// </summary>
        /// <param name="key">Key associated with the stored flag.</param>
        /// <param name="value">Value to persist.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task SetFlagAsync(string key, bool value, CancellationToken cancellationToken = default);

        /// <summary>
        /// Remove a boolean flag from the lightweight key-value store.
        /// </summary>
        /// <param name="key">Key associated with the stored flag.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task DeleteFlagAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieve legacy JSON payloads that may still exist in older storage systems
        /// (e.g. PlayerPrefs) so they can be migrated into the primary storage backend.
        /// </summary>
        /// <param name="legacyKey">Legacy key associated with the stored payload.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task<T> ReadLegacyJsonAsync<T>(string legacyKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieve an arbitrary legacy string value from older storage systems.
        /// </summary>
        /// <param name="legacyKey">Legacy key associated with the stored value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task<string> ReadLegacyValueAsync(string legacyKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Remove a legacy entry once it has been migrated into the new storage system.
        /// </summary>
        /// <param name="legacyKey">Legacy key associated with the stored payload.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        Task ClearLegacyKeyAsync(string legacyKey, CancellationToken cancellationToken = default);
    }
}
