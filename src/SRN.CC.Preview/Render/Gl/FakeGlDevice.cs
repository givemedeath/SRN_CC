namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// One entry in <see cref="FakeGlDevice.Calls"/>: the name of the <see cref="IGlDevice"/> method
/// invoked and whatever arguments the caller chose to record. A no-op call (device lost, disposed,
/// or a cross-context handle) is never recorded — <see cref="Calls"/> is a log of GL calls actually
/// issued, mirroring what the real device would have sent to the driver.
/// </summary>
public sealed record GlCallRecord(string Method, IReadOnlyList<object?> Arguments)
{
    public GlCallRecord(string method, params object?[] arguments)
        : this(method, (IReadOnlyList<object?>)arguments)
    {
    }

    public override string ToString() => $"{Method}({string.Join(", ", Arguments)})";
}

/// <summary>
/// In-memory <see cref="IGlDevice"/> test double: no real GL calls, no GPU, no Avalonia. Assigns
/// fake incrementing <see cref="uint"/> names, tracks live resources exactly like a real device
/// would, and records every issued call for assertion. Public so both <c>SRN.CC.Tests</c> and any
/// future App-layer tests can use it (architecture decision A4).
/// </summary>
public sealed class FakeGlDevice : IGlDevice
{
    private readonly List<GlCallRecord> _calls = [];
    private readonly GlResourceRegistry _registry = new();
    private readonly bool _failProgramLink;
    private uint _nextName = 1;
    private bool _isLost;
    private bool _disposed;

    public FakeGlDevice(GlCapabilities? capabilities = null, bool failProgramLink = false)
    {
        Capabilities = capabilities ?? DefaultCapabilities();
        _failProgramLink = failProgramLink;
    }

    public GlContextId ContextId { get; } = GlContextId.Next();

    public bool IsLost => _isLost;

    public GlCapabilities Capabilities { get; }

    public IReadOnlyCollection<GlHandle> LiveResources => _registry.Live;

    /// <summary>Every GL call actually issued by this device, in order, for test assertion.</summary>
    public IReadOnlyList<GlCallRecord> Calls => _calls;

    private bool ShouldNoOp => _isLost || _disposed;

    private static GlCapabilities DefaultCapabilities() => new(
        IsEmbeddedProfile: true,
        MajorVersion: 3,
        MinorVersion: 0,
        HasVertexArrayObjects: true,
        HasNonPowerOfTwoTextures: true,
        MaxTextureSize: 4096,
        GlslVersionDirective: "#version 300 es",
        RendererName: "FakeGlDevice");

    public GlHandle CreateBuffer(GlBufferTarget target, ReadOnlySpan<byte> data)
    {
        if (ShouldNoOp)
        {
            return GlHandle.Zero(GlResourceKind.Buffer);
        }

        var handle = new GlHandle(ContextId, _nextName++, GlResourceKind.Buffer);
        _registry.Track(handle);
        _calls.Add(new GlCallRecord("CreateBuffer", target, data.Length, handle.Name));
        return handle;
    }

    public GlHandle CreateTexture2D(int width, int height, ReadOnlySpan<byte> bgra, bool hasAlpha)
    {
        if (ShouldNoOp)
        {
            return GlHandle.Zero(GlResourceKind.Texture);
        }

        var handle = new GlHandle(ContextId, _nextName++, GlResourceKind.Texture);
        _registry.Track(handle);
        _calls.Add(new GlCallRecord("CreateTexture2D", width, height, bgra.Length, hasAlpha, handle.Name));
        return handle;
    }

    public GlHandle CreateProgram(string vertexSource, string fragmentSource, out string? linkLog)
    {
        if (ShouldNoOp)
        {
            linkLog = null;
            return GlHandle.Zero(GlResourceKind.Program);
        }

        if (_failProgramLink)
        {
            linkLog = "FakeGlDevice: simulated shader link failure (failProgramLink: true).";
            _calls.Add(new GlCallRecord("CreateProgram.Failed", vertexSource.Length, fragmentSource.Length));
            return GlHandle.Zero(GlResourceKind.Program);
        }

        linkLog = null;
        var handle = new GlHandle(ContextId, _nextName++, GlResourceKind.Program);
        _registry.Track(handle);
        _calls.Add(new GlCallRecord("CreateProgram", vertexSource.Length, fragmentSource.Length, handle.Name));
        return handle;
    }

    public void Delete(GlHandle handle)
    {
        if (ShouldNoOp || handle.IsZero || handle.Context != ContextId)
        {
            return;
        }

        if (!_registry.Untrack(handle))
        {
            return;
        }

        _calls.Add(new GlCallRecord("Delete", handle.Kind, handle.Name));
    }

    /// <summary>
    /// Records a clear. <see cref="ClearCount"/> is what lets a test prove the renderer clears the
    /// depth buffer before drawing, which is otherwise only observable on a real GPU as an empty
    /// viewport.
    /// </summary>
    public void Clear(int framebuffer, int viewportWidth, int viewportHeight)
    {
        if (ShouldNoOp)
        {
            return;
        }

        // Deliberately not added to the call record list: that list tracks resource lifetime, and a
        // clear creates and destroys nothing. The counter is the observable.
        ClearCount++;
        LastClearFramebuffer = framebuffer;
    }

    /// <summary>The framebuffer named by the most recent <see cref="Clear"/>.</summary>
    public int LastClearFramebuffer { get; private set; } = -1;

    /// <summary>How many times <see cref="Clear"/> has been issued against this device.</summary>
    public int ClearCount { get; private set; }

    public void Draw(in GlDrawCall call)
    {
        if (ShouldNoOp)
        {
            return;
        }

        if (call.Program.Context != ContextId
            || call.VertexBuffer.Context != ContextId
            || call.IndexBuffer.Context != ContextId
            || (call.Texture is { } textureHandle && textureHandle.Context != ContextId))
        {
            return;
        }

        _calls.Add(new GlCallRecord(
            "Draw",
            call.Program.Name,
            call.VertexBuffer.Name,
            call.IndexBuffer.Name,
            call.IndexCount,
            call.Texture?.Name,
            call.AlphaBlendEnabled));
    }

    public void MarkLost()
    {
        if (_isLost)
        {
            return;
        }

        // Mirrors SilkGlDevice exactly: abandon, do not delete. No call is recorded here because a
        // lost context receives zero real GL calls — that is the entire point of the test double.
        _isLost = true;
        _registry.Clear();
    }

    /// <summary>Test hook equivalent to what a real context-loss event (<c>OnOpenGlLost</c>) would trigger.</summary>
    public void SimulateContextLoss() => MarkLost();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Delete while the device still considers itself live, so each Delete() call is recorded
        // normally; only then flip _disposed so every later call becomes a no-op.
        if (!_isLost)
        {
            foreach (GlHandle handle in _registry.Live.ToArray())
            {
                Delete(handle);
            }
        }

        _disposed = true;
        _registry.Clear();
    }
}
