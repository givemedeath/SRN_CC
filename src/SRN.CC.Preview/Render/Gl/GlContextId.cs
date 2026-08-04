using System.Threading;

namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// Identifies one GL context lifetime. A fresh <see cref="GlContextId"/> is minted every time a new
/// <see cref="IGlDevice"/> is constructed (e.g. once per <c>OnOpenGlInit</c> on the Avalonia side),
/// so a renderer built against one device can never be mistaken for belonging to a later,
/// unrelated context after context loss and recreation.
/// </summary>
/// <remarks>
/// Architecture decision A4: the cross-context guard compares a resource/device's
/// <see cref="GlContextId"/> against the current device's <see cref="IGlDevice.ContextId"/>; a
/// mismatch means the caller is holding handles from a dead context and must not issue any GL call
/// against them.
/// </remarks>
public readonly record struct GlContextId(long Value)
{
    private static long _counter;

    /// <summary>Reserved for "no context" (e.g. a default-constructed <see cref="GlHandle"/>).</summary>
    public static readonly GlContextId None = default;

    /// <summary>Mints the next monotonically increasing context id, starting from 1.</summary>
    public static GlContextId Next() => new(Interlocked.Increment(ref _counter));
}
