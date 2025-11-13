namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Minimal logging abstraction so services can emit diagnostics without
    /// depending on UnityEngine.Debug.
    /// </summary>
    public interface IWalletLogger
    {
        void Log(string message);
        void LogWarning(string message);
        void LogError(string message);
    }
}
