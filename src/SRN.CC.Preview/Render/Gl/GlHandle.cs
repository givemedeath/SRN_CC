namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// A named GL object (buffer, texture, program, ...) tagged with the <see cref="GlContextId"/> of
/// the device that created it. The tag is what makes cross-context use structurally detectable:
/// a handle minted by device A compared against device B's <see cref="IGlDevice.ContextId"/> will
/// never match, so callers can refuse to touch it instead of issuing a GL call against a dead or
/// unrelated context.
/// </summary>
/// <param name="Context">The context that owns this name.</param>
/// <param name="Name">The underlying GL object name. Zero denotes a failed/absent allocation.</param>
/// <param name="Kind">What kind of GL object <paramref name="Name"/> refers to.</param>
public readonly record struct GlHandle(GlContextId Context, uint Name, GlResourceKind Kind)
{
    /// <summary>True when this handle does not refer to a real, successfully created GL object.</summary>
    public bool IsZero => Name == 0;

    /// <summary>A zero handle of the given kind, carrying no context. Used for failure returns.</summary>
    public static GlHandle Zero(GlResourceKind kind) => new(GlContextId.None, 0, kind);
}

/// <summary>The kind of GL object a <see cref="GlHandle"/> names.</summary>
public enum GlResourceKind
{
    Buffer,
    VertexArray,
    Texture,
    Program,
    Shader,
}
