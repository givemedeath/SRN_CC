namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// Tracks the set of live <see cref="GlHandle"/> values for one <see cref="IGlDevice"/> instance.
/// Shared bookkeeping used by both <see cref="SilkGlDevice"/> and <see cref="FakeGlDevice"/> so the
/// "track on create, untrack on delete, clear-without-deleting on context loss" rule is implemented
/// exactly once. Not part of the public device contract — an implementation detail.
/// </summary>
internal sealed class GlResourceRegistry
{
    private readonly HashSet<GlHandle> _live = [];

    public IReadOnlyCollection<GlHandle> Live => _live;

    /// <summary>Records a newly created, non-zero handle as live.</summary>
    public void Track(GlHandle handle)
    {
        if (!handle.IsZero)
        {
            _live.Add(handle);
        }
    }

    /// <summary>Removes a handle from the live set. Returns true when it was tracked.</summary>
    public bool Untrack(GlHandle handle) => _live.Remove(handle);

    /// <summary>Forgets every tracked handle without any external side effect (context-loss path).</summary>
    public void Clear() => _live.Clear();
}
