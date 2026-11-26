namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Target stash container kind for Ctrl+Click affinity routing (<c>StashControl + 0x580</c>).
/// </summary>
public enum StashKind : byte
{
    /// <summary>Personal player stash (<c>0</c>).</summary>
    Personal = 0,

    /// <summary>Guild stash (<c>1</c>).</summary>
    Guild = 1,
}

/// <summary>
/// Context supplied to <see cref="IStashAffinityService"/> routers and stock affinity rules when evaluating
/// a Ctrl+Click stash transfer (<c>ui.stash_get_affinity_targets</c> / <c>items.compute_affinity_bitset</c>).
/// </summary>
/// <param name="Item">Snapshot of the item being transferred.</param>
/// <param name="StashKind">Whether the target stash is personal or guild.</param>
/// <param name="IsMoveAll"><c>true</c> for Ctrl+RMB Move-All transfers.</param>
/// <param name="StashControlHandle">Native <c>StashControl*</c> handle, or <see cref="nint.Zero"/> when simulated.</param>
public sealed record StashAffinityItemContext(
    HoveredItemInfo Item,
    StashKind StashKind = StashKind.Personal,
    bool IsMoveAll = false,
    nint StashControlHandle = 0);

/// <summary>
/// Target stash tab route injected into <c>StashControl::GetAffinityTargets</c> and transmitted in <c>C→S 0x00E1</c>.
/// Bypasses the stock 22-affinity cap (<c>0..21</c>) by targeting any arbitrary <paramref name="TabIndex"/>.
/// </summary>
/// <param name="TabIndex">Zero-based destination stash tab index sent verbatim in <c>C→S 0x00E1</c>.</param>
/// <param name="AffinityId">Optional <c>StashTabAffinityId</c> byte carried alongside the route (defaults to <c>0</c>).</param>
/// <param name="ReplaceStockTargets">When <c>true</c>, replaces stock affinity targets rather than prepending/appending.</param>
public sealed record StashAffinityRoute(
    ushort TabIndex,
    byte AffinityId = 0,
    bool ReplaceStockTargets = false);

/// <summary>
/// Intercepts Ctrl+Click stash affinity routing (<c>ui.stash_get_affinity_targets</c> <c>0xa83460</c>)
/// and stock affinity bitset calculation (<c>items.compute_affinity_bitset</c> <c>0x1589b20</c>)
/// so mods can route custom <c>ItemClass</c> or predicate-matched items to arbitrary stash tabs (<c>game.StashAffinity</c>).
/// </summary>
public interface IStashAffinityService
{
    /// <summary>Maximum valid stock <c>StashTabAffinityId</c> bit index (<c>0..21</c>).</summary>
    const byte MaxStockAffinityId = 21;

    /// <summary>
    /// Registers a custom Ctrl+Click stash router evaluated in ascending <paramref name="priority"/> order.
    /// Returning a non-null <see cref="StashAffinityRoute"/> injects that target <see cref="StashAffinityRoute.TabIndex"/>
    /// into <c>C→S 0x00E1</c> (bypassing the stock <c>0..21</c> affinity cap).
    /// </summary>
    /// <param name="id">Unique route identifier.</param>
    /// <param name="router">Predicate/router returning a target route or <c>null</c> to pass.</param>
    /// <param name="priority">Evaluation priority (lower values run first).</param>
    /// <returns>Disposable handle that unregisters the route.</returns>
    IDisposable RegisterRoute(string id, Func<StashAffinityItemContext, StashAffinityRoute?> router, int priority = 0);

    /// <summary>
    /// Registers a rule mapping custom items into a stock <c>StashTabAffinityId</c> bit (<c>0..21</c>) inside
    /// <c>items.compute_affinity_bitset</c> (<c>0x1589b20</c>) so stock stash tab affinity checkboxes apply natively.
    /// </summary>
    /// <param name="id">Unique rule identifier.</param>
    /// <param name="affinitySelector">Callback returning a stock affinity bit id (<c>0..21</c>) or <c>null</c>.</param>
    /// <param name="priority">Evaluation priority (lower values run first).</param>
    /// <returns>Disposable handle that unregisters the rule.</returns>
    IDisposable RegisterStockAffinityRule(string id, Func<StashAffinityItemContext, byte?> affinitySelector, int priority = 0);

    /// <summary>Evaluates all registered custom routes for <paramref name="context"/> in priority order.</summary>
    IReadOnlyList<StashAffinityRoute> EvaluateRoutes(StashAffinityItemContext context);

    /// <summary>Evaluates all registered stock affinity rules for <paramref name="context"/>, returning the OR-ed 22-bit mask.</summary>
    uint EvaluateStockAffinityMask(StashAffinityItemContext context);
}
