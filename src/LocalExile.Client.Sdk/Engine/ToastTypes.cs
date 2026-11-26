namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Visual severity level for HUD toast notifications displayed by <see cref="IToastService"/>.
/// </summary>
public enum ToastSeverity
{
    /// <summary>Informational notification (neutral/white styling).</summary>
    Info = 0,

    /// <summary>Success notification (green styling).</summary>
    Success = 1,

    /// <summary>Warning notification (amber/gold styling).</summary>
    Warning = 2,

    /// <summary>Error notification (red styling).</summary>
    Error = 3,
}

/// <summary>
/// Snapshot of an active or queued HUD toast notification.
/// </summary>
/// <param name="Id">Monotonic unique toast identifier.</param>
/// <param name="Title">Toast header/title line.</param>
/// <param name="Message">Toast body text.</param>
/// <param name="Severity">Visual severity tier.</param>
/// <param name="Duration">Total display duration.</param>
/// <param name="RemainingSeconds">Remaining display time in seconds.</param>
/// <param name="OwnerModId">Identifier of the mod (or <c>"host"</c>) that created this toast.</param>
public sealed record ToastMessage(
    ulong Id,
    string Title,
    string Message,
    ToastSeverity Severity,
    TimeSpan Duration,
    float RemainingSeconds,
    string OwnerModId = "host");

/// <summary>
/// NativeMods-owned HUD toast notification overlay anchored to <see cref="ScreenSlot.HudTopRight"/>.
/// Supports queued stacking toasts, severity styling, frame-tick auto-dismissal, and clean teardown on unload/disconnect.
/// </summary>
public interface IToastService
{
    /// <summary>Currently visible toasts ordered top-to-bottom.</summary>
    IReadOnlyList<ToastMessage> ActiveToasts { get; }

    /// <summary>Number of additional toasts waiting in the FIFO queue behind <see cref="ActiveToasts"/>.</summary>
    int QueuedCount { get; }

    /// <summary>Maximum number of concurrently visible stacked toasts before additional toasts queue.</summary>
    int MaxVisibleToasts { get; set; }

    /// <summary>Default display duration used when <see cref="Show"/> omits an explicit duration.</summary>
    TimeSpan DefaultDuration { get; set; }

    /// <summary>
    /// Enqueues or displays a toast notification on the HUD overlay.
    /// </summary>
    /// <param name="title">Short header line.</param>
    /// <param name="message">Body message text.</param>
    /// <param name="severity">Visual severity level.</param>
    /// <param name="duration">Optional explicit display duration; defaults to <see cref="DefaultDuration"/>.</param>
    /// <returns>The monotonic toast identifier.</returns>
    ulong Show(string title, string message, ToastSeverity severity = ToastSeverity.Info, TimeSpan? duration = null);

    /// <summary>Dismisses an active or queued toast immediately by id.</summary>
    /// <returns><c>true</c> if the toast existed and was removed.</returns>
    bool Dismiss(ulong toastId);

    /// <summary>Dismisses all visible and queued toasts (scoped to the calling mod when accessed via a mod context).</summary>
    void Clear();
}
