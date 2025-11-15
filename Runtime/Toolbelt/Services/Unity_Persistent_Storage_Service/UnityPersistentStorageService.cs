using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Default storage implementation for the Solana Toolbelt when running inside Unity.
    /// JSON payloads are written to the persistent data path while lightweight flags are
    /// persisted via PlayerPrefs. The implementation is asynchronous to keep expensive
    /// disk I/O off the main thread and provides extensibility points for alternative
    /// backends (IndexedDB, cloud storage, etc.).
    /// </summary>
    [DisallowMultipleComponent]
    public class UnityPersistentStorageService : MonoBehaviour, ISolanaStorageService
    {
        [Tooltip("Optional subdirectory created beneath Application.persistentDataPath for toolbelt data.")]
        [SerializeField] private string storageFolder = "SolanaToolbelt";

        [Tooltip("When enabled the storage directory is created when the component awakens.")]
        [SerializeField] private bool ensureStorageDirectoryOnAwake = true;

        private string resolvedRootPath;

        /// <summary>
        /// Returns the absolute directory used for JSON payloads. The value is cached after the first lookup.
        /// </summary>
        public string RootPath
        {
            get
            {
                if (!string.IsNullOrEmpty(resolvedRootPath))
                {
                    return resolvedRootPath;
                }

                var basePath = Application.persistentDataPath;
                resolvedRootPath = string.IsNullOrWhiteSpace(storageFolder)
                    ? basePath
                    : Path.Combine(basePath, storageFolder);

                return resolvedRootPath;
            }
        }

        private void Awake()
        {
            if (ensureStorageDirectoryOnAwake)
            {
                EnsureRootDirectoryExists();
            }
        }

        /// <summary>
        /// Allows external systems to override the root directory at runtime (useful for tests).
        /// </summary>
        public void OverrideRootPath(string absolutePath)
        {
            resolvedRootPath = absolutePath;
            if (ensureStorageDirectoryOnAwake)
            {
                EnsureRootDirectoryExists();
            }
        }

        private static bool SupportsBackgroundThreads
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return false;
#else
                return true;
#endif
            }
        }

        public Task<T> ReadJsonAsync<T>(string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("Identifier must be provided", nameof(identifier));
            }

            string path = ResolveJsonPath(identifier);
            if (SupportsBackgroundThreads)
            {
                return Task.Run(() => ReadJsonInternal<T>(identifier, path, cancellationToken), cancellationToken);
            }

            return Task.FromResult(ReadJsonInternal<T>(identifier, path, cancellationToken));
        }

        public Task WriteJsonAsync<T>(string identifier, T payload, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("Identifier must be provided", nameof(identifier));
            }

            EnsureRootDirectoryExists();

            string path = ResolveJsonPath(identifier);
            if (SupportsBackgroundThreads)
            {
                return Task.Run(() => WriteJsonInternal(path, payload, cancellationToken), cancellationToken);
            }

            WriteJsonInternal(path, payload, cancellationToken);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("Identifier must be provided", nameof(identifier));
            }

            string path = ResolveJsonPath(identifier);
            if (SupportsBackgroundThreads)
            {
                return Task.Run(() => DeleteInternal(path, cancellationToken), cancellationToken);
            }

            DeleteInternal(path, cancellationToken);
            return Task.CompletedTask;
        }

        private T ReadJsonInternal<T>(string identifier, string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                return default;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch
            {
                Debug.LogWarning($"[UnityPersistentStorageService] Failed to deserialize '{identifier}'.");
                return default;
            }
        }

        private void WriteJsonInternal<T>(string path, T payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string json = JsonUtility.ToJson(payload ?? Activator.CreateInstance<T>());
            File.WriteAllText(path, json);
        }

        private void DeleteInternal(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public Task<bool> GetFlagAsync(string key, bool defaultValue = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key must be provided", nameof(key));
            }

            bool result = PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;
            return Task.FromResult(result);
        }

        public Task SetFlagAsync(string key, bool value, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key must be provided", nameof(key));
            }

            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
            return Task.CompletedTask;
        }

        public Task DeleteFlagAsync(string key, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key must be provided", nameof(key));
            }

            if (PlayerPrefs.HasKey(key))
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }

            return Task.CompletedTask;
        }

        public Task<T> ReadLegacyJsonAsync<T>(string legacyKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(legacyKey))
            {
                throw new ArgumentException("Legacy key must be provided", nameof(legacyKey));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!PlayerPrefs.HasKey(legacyKey))
            {
                return Task.FromResult(default(T));
            }

            string json = PlayerPrefs.GetString(legacyKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Task.FromResult(default(T));
            }

            T result;
            try
            {
                result = JsonUtility.FromJson<T>(json);
            }
            catch
            {
                Debug.LogWarning($"[UnityPersistentStorageService] Failed to deserialize legacy entry '{legacyKey}'.");
                result = default;
            }

            return Task.FromResult(result);
        }

        public Task<string> ReadLegacyValueAsync(string legacyKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(legacyKey))
            {
                throw new ArgumentException("Legacy key must be provided", nameof(legacyKey));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PlayerPrefs.HasKey(legacyKey) ? PlayerPrefs.GetString(legacyKey, string.Empty) : null);
        }

        public Task ClearLegacyKeyAsync(string legacyKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(legacyKey))
            {
                throw new ArgumentException("Legacy key must be provided", nameof(legacyKey));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (PlayerPrefs.HasKey(legacyKey))
            {
                PlayerPrefs.DeleteKey(legacyKey);
            }

            PlayerPrefs.Save();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Attempts to retrieve the size in bytes of the JSON document stored for the given identifier.
        /// Returns null when the file does not exist or the backend does not expose a size measurement.
        /// </summary>
        public long? TryGetFileSize(string identifier)
        {
            string path = ResolveJsonPath(identifier);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return null;
            }
        }

        protected virtual string ResolveJsonPath(string identifier)
        {
            return Path.Combine(RootPath, identifier);
        }

        protected virtual void EnsureRootDirectoryExists()
        {
            string root = RootPath;
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }
        }
    }
}
