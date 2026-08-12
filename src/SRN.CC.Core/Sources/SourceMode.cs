namespace SRN.CC.Core.Sources;

/// <summary>
/// Controls how a source participates in resolution, selection, and the asset list.
/// </summary>
public enum SourceMode
{
    /// <summary>
    /// Default. The source is visible, its occurrences compete in automatic winner
    /// resolution by priority, and its resolved assets are selectable for building.
    /// </summary>
    Full = 0,

    /// <summary>
    /// The source is visible and its occurrences are valid pin targets, but it never
    /// wins resolution automatically (a lower-priority <see cref="Full"/> source wins
    /// instead) and its assets are never auto-selected. The only way a reference
    /// occurrence becomes a winner is an explicit pin; a pinned reference winner is an
    /// ordinary selectable, buildable asset.
    /// </summary>
    Reference = 1,

    /// <summary>
    /// The source is fully excluded: its rows are hidden from the asset list, its
    /// occurrences drop out of resolution and conflict-flag computation, and any pin
    /// into it becomes invalid (flagged, never silently reassigned or removed).
    /// </summary>
    Hidden = 2
}
