using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Provides pricing information for level editor monetization flows.
    /// </summary>
    public interface ILevelPricingData
    {
        /// <summary>
        /// Collection of pricing options presented to the player.
        /// </summary>
        IReadOnlyList<object> LevelEditorPricingOptions { get; }
    }

    /// <summary>
    /// Provides callbacks that bridge Solana Toolbelt flows into the host game's UI layer.
    /// Implementations live outside the Toolbelt assembly and translate these requests into
    /// concrete popup interactions.
    /// </summary>
    public interface IToolbeltUiBridge
    {
        void ShowPricePopup(
            IReadOnlyList<object> prices,
            Action<object> onSelected,
            Action onCanceled = null);

        void ShowMintCostPopup(
            object selectedOption,
            object configurationContext,
            Func<Task> onConfirm,
            Func<Task> onCancel);

        void ShowConfirmCancelPopup(
            string message,
            Func<Task> onConfirm,
            Func<Task> onCancel);

        void ShowWalletConnectPopup(
            object configurationContext,
            Action onConfirm,
            Action onCancel);

        IToolbeltMintProgressHandle ShowMintProgressPopup(string title, Action onCancelCallback);

        IToolbeltProcessingHandle ShowProcessingPopup(string title, Action onCancelCallback);

        void ShowFailurePopup(string message, Action onDismiss = null);

        void ShowFailurePopup(
            string message,
            IReadOnlyList<(string label, Action callback)> callToActions,
            Action onDismiss = null);

        void ShowToast(string message, float durationSeconds = 2f);

        /// <summary>
        /// Display the level mint popup for user-generated content. The payload
        /// is expected to be an object understood by the host game's UI layer.
        /// </summary>
        /// <param name="mintRequest">Context object describing the level mint request.</param>
        void ShowLevelMintPopup(object mintRequest);
    }

    /// <summary>
    /// Represents a controllable mint progress UI element.
    /// </summary>
    public interface IToolbeltMintProgressHandle
    {
        void SetStatusSending();
        void SetStatusConfirming();
        void SetStatusComplete();
        void SetStatusFailed(string errorMessage = null);
    }

    /// <summary>
    /// Represents a controllable processing popup used for token transactions.
    /// </summary>
    public interface IToolbeltProcessingHandle
    {
        void SetStatusSending();
        void SetStatusConfirming();
        void SetStatusComplete(string successMessage = null);
        void SetStatusFailed(string failureMessage = null);
        void ConfigureBuyButton(string label, Action onClick, bool visible);
    }
}
