namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// A GPU device abstraction over one GL context lifetime. Two implementations exist:
/// <see cref="SilkGlDevice"/> (real, backed by <c>Silk.NET.OpenGL</c>) and <see cref="FakeGlDevice"/>
/// (in-memory test double, no GPU). Callers — chiefly <see cref="ModelRenderer"/> — must never branch
/// on which implementation they hold; both obey identical lifetime and no-op-when-lost semantics.
/// </summary>
/// <remarks>
/// Architecture decision A4: on context loss, the driver has already invalidated every GL name in
/// this context. <see cref="MarkLost"/> must therefore forget all tracked resources without issuing
/// a single GL call — calling <c>glDelete*</c> against a dead context is how you get a crash or a
/// spurious driver error, not a clean release.
/// </remarks>
public interface IGlDevice : IDisposable
{
    /// <summary>The id minted for this device's context at construction time.</summary>
    GlContextId ContextId { get; }

    /// <summary>True once <see cref="MarkLost"/> has been called; every subsequent operation is a no-op.</summary>
    bool IsLost { get; }

    /// <summary>The capability snapshot probed at construction time.</summary>
    GlCapabilities Capabilities { get; }

    /// <summary>
    /// Creates and uploads a GPU buffer. Returns a zero handle without issuing any GL call when
    /// <see cref="IsLost"/> is true.
    /// </summary>
    GlHandle CreateBuffer(GlBufferTarget target, ReadOnlySpan<byte> data);

    /// <summary>
    /// Creates and uploads a 2D texture from packed BGRA8 pixel data. Returns a zero handle without
    /// issuing any GL call when <see cref="IsLost"/> is true.
    /// </summary>
    GlHandle CreateTexture2D(int width, int height, ReadOnlySpan<byte> bgra, bool hasAlpha);

    /// <summary>
    /// Compiles and links a shader program. Never throws: on any compile or link failure this
    /// returns a handle with <c>Name == 0</c> and a non-null <paramref name="linkLog"/> describing
    /// the failure, so callers (<see cref="ModelRenderer.Initialize"/>) can degrade gracefully.
    /// </summary>
    GlHandle CreateProgram(string vertexSource, string fragmentSource, out string? linkLog);

    /// <summary>
    /// Releases one previously created resource. A no-op when <see cref="IsLost"/>, when the handle
    /// is zero, or when the handle was not created by this device.
    /// </summary>
    void Delete(GlHandle handle);

    /// <summary>
    /// Clears the colour and depth of <paramref name="framebuffer"/> before a frame's draw calls.
    /// A no-op when <see cref="IsLost"/>.
    /// </summary>
    /// <remarks>
    /// Not optional, and not merely cosmetic. The host hands over a framebuffer whose depth
    /// attachment holds undefined content, and the device enables depth testing for its whole
    /// lifetime — so without a depth clear the comparison runs against whatever was already there
    /// and can reject every fragment of every mesh. That failure is invisible from outside GL: the
    /// draw calls are all issued and all silently discarded, which is precisely how a model with
    /// valid geometry and bound textures renders as an empty viewport.
    /// </remarks>
    void Clear(int framebuffer, int viewportWidth, int viewportHeight);

    /// <summary>Issues one indexed draw call. A no-op when <see cref="IsLost"/>.</summary>
    void Draw(in GlDrawCall call);

    /// <summary>
    /// Marks the device lost: <see cref="IsLost"/> becomes true and every tracked resource is
    /// forgotten (<see cref="LiveResources"/> becomes empty) without issuing any GL call. Every
    /// subsequent call on this device is a safe no-op.
    /// </summary>
    void MarkLost();

    /// <summary>All resources created by this device that have not yet been deleted or forgotten. Leak-assertion hook.</summary>
    IReadOnlyCollection<GlHandle> LiveResources { get; }
}

/// <summary>Which GL buffer binding target a buffer created via <see cref="IGlDevice.CreateBuffer"/> is intended for.</summary>
public enum GlBufferTarget
{
    Vertex,
    Index,
}
