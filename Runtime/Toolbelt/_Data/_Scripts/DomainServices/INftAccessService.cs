using System;
using System.Threading.Tasks;
using UnityEngine.Events;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Defines the contract used by game systems to interact with NFT-based access flows.
    /// </summary>
    public interface INftAccessService
    {
        /// <summary>
        /// Gets the UnityEvent that is raised whenever access is granted locally.
        /// </summary>
        UnityEvent AccessGranted { get; }

        /// <summary>
        /// Gets the UnityEvent that is raised whenever access is revoked locally.
        /// </summary>
        UnityEvent AccessRevoked { get; }

        /// <summary>
        /// Returns whether the feature is currently unlocked for the local player.
        /// </summary>
        bool IsLocallyUnlocked();

        /// <summary>
        /// Forces an ownership refresh by querying on-chain state.
        /// </summary>
        Task RefreshOwnershipAsync();

        /// <summary>
        /// Runs the full unlock flow, prompting the user to mint if necessary.
        /// </summary>
        Task AttemptUnlockFlow(Action onFailure = null, Action onCancel = null);
    }
}
