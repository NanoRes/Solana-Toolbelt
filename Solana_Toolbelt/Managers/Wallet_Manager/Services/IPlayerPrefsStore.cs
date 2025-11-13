namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Abstraction over PlayerPrefs used to keep wallet services free from
    /// UnityEngine dependencies, enabling unit testing in .NET environments.
    /// </summary>
    public interface IPlayerPrefsStore
    {
        bool HasKey(string key);
        string GetString(string key, string defaultValue = "");
        void SetString(string key, string value);
        void DeleteKey(string key);
        void Save();
    }
}
