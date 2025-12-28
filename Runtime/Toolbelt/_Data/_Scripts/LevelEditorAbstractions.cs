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
        /// <summary>
        /// Prompt the player to select a pricing option for the level editor flow.
        /// </summary>
        /// <param name="prices">Collection of pricing options to display.</param>
        /// <param name="onSelected">
        /// Callback invoked after the player selects an option. The selected option must be
        /// one of the provided <paramref name="prices"/> entries.
        /// </param>
        /// <param name="onCanceled">Optional callback when the player closes the popup.</param>
        void ShowPricePopup(
            IReadOnlyList<object> prices,
            Action<object> onSelected,
            Action onCanceled = null);

        /// <summary>
        /// Show a cost confirmation popup for the selected minting option.
        /// </summary>
        /// <param name="selectedOption">The pricing option chosen by the player.</param>
        /// <param name="configurationContext">
        /// Context object describing the current mint configuration (e.g., network, wallet state).
        /// </param>
        /// <param name="onConfirm">Async callback for confirming and starting the mint.</param>
        /// <param name="onCancel">Async callback for cancelling and returning to the previous step.</param>
        void ShowMintCostPopup(
            object selectedOption,
            object configurationContext,
            Func<Task> onConfirm,
            Func<Task> onCancel);

        /// <summary>
        /// Show a generic confirmation popup with a custom message.
        /// </summary>
        /// <param name="message">Message to show to the player.</param>
        /// <param name="onConfirm">Async callback for the affirmative action.</param>
        /// <param name="onCancel">Async callback for the negative action.</param>
        void ShowConfirmCancelPopup(
            string message,
            Func<Task> onConfirm,
            Func<Task> onCancel);

        /// <summary>
        /// Ask the player to connect a wallet or acknowledge a wallet requirement.
        /// </summary>
        /// <param name="configurationContext">Context describing the wallet/network requirements.</param>
        /// <param name="onConfirm">Callback invoked when the player confirms.</param>
        /// <param name="onCancel">Callback invoked when the player cancels.</param>
        void ShowWalletConnectPopup(
            object configurationContext,
            Action onConfirm,
            Action onCancel);

        /// <summary>
        /// Show an in-progress mint popup with a cancellable action.
        /// </summary>
        /// <param name="title">Title displayed on the progress UI.</param>
        /// <param name="onCancelCallback">Callback invoked when the player cancels.</param>
        /// <returns>
        /// Handle used to update the mint progress state (sending, confirming, complete, failed).
        /// </returns>
        IToolbeltMintProgressHandle ShowMintProgressPopup(string title, Action onCancelCallback);

        /// <summary>
        /// Show a processing popup for token transactions with a cancellable action.
        /// </summary>
        /// <param name="title">Title displayed on the processing UI.</param>
        /// <param name="onCancelCallback">Callback invoked when the player cancels.</param>
        /// <returns>
        /// Handle used to update processing state (sending, confirming, complete, failed) and CTA.
        /// </returns>
        IToolbeltProcessingHandle ShowProcessingPopup(string title, Action onCancelCallback);

        /// <summary>
        /// Show a failure popup with a dismiss action.
        /// </summary>
        /// <param name="message">Failure message to display.</param>
        /// <param name="onDismiss">Optional callback invoked when dismissed.</param>
        void ShowFailurePopup(string message, Action onDismiss = null);

        /// <summary>
        /// Show a failure popup with call-to-action buttons.
        /// </summary>
        /// <param name="message">Failure message to display.</param>
        /// <param name="callToActions">Buttons to present with labels and callbacks.</param>
        /// <param name="onDismiss">Optional callback invoked when dismissed.</param>
        void ShowFailurePopup(
            string message,
            IReadOnlyList<(string label, Action callback)> callToActions,
            Action onDismiss = null);

        /// <summary>
        /// Show a short-lived toast notification.
        /// </summary>
        /// <param name="message">Message to display.</param>
        /// <param name="durationSeconds">Duration in seconds before dismissal.</param>
        void ShowToast(string message, float durationSeconds = 2f);

        /// <summary>
        /// Display the level mint popup for user-generated content. The payload
        /// is expected to be an object understood by the host game's UI layer.
        /// </summary>
        /// <param name="mintRequest">Context object describing the level mint request.</param>
        void ShowLevelMintPopup(object mintRequest);

        /// <summary>
        /// Minimal Unity UI pseudo-implementation for these callbacks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Typical flow: ShowPricePopup -> ShowMintCostPopup -> ShowMintProgressPopup -> ShowProcessingPopup.
        /// Ensure each UI closes or transitions once callbacks resolve.
        /// </para>
        /// <code>
        /// public class ToolbeltUiBridge : IToolbeltUiBridge {
        ///   public void ShowPricePopup(IReadOnlyList<object> prices, Action<object> onSelected, Action onCanceled) {
        ///     pricePopup.Show(prices, option => { pricePopup.Hide(); onSelected(option); }, () => { pricePopup.Hide(); onCanceled?.Invoke(); });
        ///   }
        ///
        ///   public void ShowMintCostPopup(object selectedOption, object context, Func&lt;Task&gt; onConfirm, Func&lt;Task&gt; onCancel) {
        ///     mintCostPopup.Show(selectedOption, async () => { await onConfirm(); mintCostPopup.Hide(); },
        ///       async () => { await onCancel(); mintCostPopup.Hide(); });
        ///   }
        ///
        ///   public IToolbeltMintProgressHandle ShowMintProgressPopup(string title, Action onCancel) {
        ///     return mintProgressPopup.Show(title, () => { onCancel?.Invoke(); mintProgressPopup.Hide(); });
        ///   }
        /// }
        /// </code>
        /// </remarks>
    }

    /// <summary>
    /// Represents a controllable mint progress UI element.
    /// </summary>
    public interface IToolbeltMintProgressHandle
    {
        /// <summary>
        /// UI should indicate the mint transaction is being sent to the network.
        /// </summary>
        void SetStatusSending();

        /// <summary>
        /// UI should indicate the mint transaction is awaiting confirmation/finalization.
        /// </summary>
        void SetStatusConfirming();

        /// <summary>
        /// UI should show a successful completion state and allow the player to dismiss.
        /// </summary>
        void SetStatusComplete();

        /// <summary>
        /// UI should show a failure state and expose the error message if provided.
        /// </summary>
        void SetStatusFailed(string errorMessage = null);
    }

    /// <summary>
    /// Represents a controllable processing popup used for token transactions.
    /// </summary>
    public interface IToolbeltProcessingHandle
    {
        /// <summary>
        /// UI should indicate the transaction is being sent to the network.
        /// </summary>
        void SetStatusSending();

        /// <summary>
        /// UI should indicate the transaction is awaiting confirmation/finalization.
        /// </summary>
        void SetStatusConfirming();

        /// <summary>
        /// UI should show a successful completion state with an optional message.
        /// </summary>
        void SetStatusComplete(string successMessage = null);

        /// <summary>
        /// UI should show a failure state with an optional message.
        /// </summary>
        void SetStatusFailed(string failureMessage = null);

        /// <summary>
        /// Configure a call-to-action button while processing (e.g., "Buy", "Open Explorer").
        /// </summary>
        /// <param name="label">Button label.</param>
        /// <param name="onClick">Callback when clicked.</param>
        /// <param name="visible">Whether the button should be visible.</param>
        void ConfigureBuyButton(string label, Action onClick, bool visible);
    }
}
