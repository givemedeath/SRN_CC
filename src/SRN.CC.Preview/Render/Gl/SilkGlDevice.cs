using System.Buffers;
using System.Numerics;
using Silk.NET.OpenGL;

namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// The real <see cref="IGlDevice"/> implementation, backed by <c>Silk.NET.OpenGL</c>. Constructed
/// from the <c>Func&lt;string, IntPtr&gt;</c> proc-address delegate that Avalonia's
/// <c>OpenGlControlBase.OnOpenGlInit(GlInterface gl)</c> exposes via <c>gl.GetProcAddress</c> — this
/// is the sole coupling point between the render layer and Avalonia (architecture decision A1/A4).
/// </summary>
/// <remarks>
/// Vertex buffers created via <see cref="CreateBuffer"/> with <see cref="GlBufferTarget.Vertex"/>
/// are expected to hold interleaved position/normal/texcoord data matching
/// <see cref="ShaderSources"/>'s attribute layout (stride 32 bytes: 3 + 3 + 2 floats).
/// </remarks>
public sealed class SilkGlDevice : IGlDevice
{
    // Fixed headlight: a world-space directional light standing in for a camera-attached headlight
    // for this MVP (milestone-6 scope: "one fixed headlight directional light plus ambient").
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.4f));
    private static readonly Vector3 AmbientColor = new(0.35f, 0.35f, 0.35f);

    private const int PositionAttributeLocation = 0;
    private const int NormalAttributeLocation = 1;
    private const int TexCoordAttributeLocation = 2;
    private const uint VertexStrideBytes = (3 + 3 + 2) * sizeof(float);

    private readonly GL _gl;
    private readonly GlResourceRegistry _registry = new();
    private bool _isLost;
    private bool _disposed;

    public GlContextId ContextId { get; }

    public bool IsLost => _isLost;

    public GlCapabilities Capabilities { get; }

    public IReadOnlyCollection<GlHandle> LiveResources => _registry.Live;

    public SilkGlDevice(Func<string, IntPtr> getProcAddress)
    {
        ContextId = GlContextId.Next();
        _gl = GL.GetApi(getProcAddress);
        Capabilities = GlCapabilities.Probe(_gl);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
    }

    private bool ShouldNoOp => _isLost || _disposed;

    public GlHandle CreateBuffer(GlBufferTarget target, ReadOnlySpan<byte> data)
    {
        if (ShouldNoOp)
        {
            return GlHandle.Zero(GlResourceKind.Buffer);
        }

        BufferTargetARB glTarget = target == GlBufferTarget.Vertex
            ? BufferTargetARB.ArrayBuffer
            : BufferTargetARB.ElementArrayBuffer;

        uint name = _gl.GenBuffer();
        _gl.BindBuffer(glTarget, name);
        _gl.BufferData(glTarget, data, BufferUsageARB.StaticDraw);
        _gl.BindBuffer(glTarget, 0);

        var handle = new GlHandle(ContextId, name, GlResourceKind.Buffer);
        _registry.Track(handle);
        return handle;
    }

    public GlHandle CreateTexture2D(int width, int height, ReadOnlySpan<byte> bgra, bool hasAlpha)
    {
        if (ShouldNoOp)
        {
            return GlHandle.Zero(GlResourceKind.Texture);
        }

        uint name = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, name);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);

        if (width > 0 && height > 0 && !bgra.IsEmpty)
        {
            // Swizzled to RGBA on the CPU and uploaded as GL_RGBA/GL_RGBA rather than handed over as
            // GL_BGRA. OpenGL ES requires glTexImage2D's internal format to equal its format, so the
            // GL_RGBA-internal/GL_BGRA-format pairing this used to pass is GL_INVALID_OPERATION on an
            // ES context: the call is rejected, the texture keeps its undefined contents, and every
            // sample of it comes back black. Nothing reports that — the texture name is valid and
            // binds fine, so a model renders as a black silhouette with all its draw calls issued.
            //
            // GL_BGRA is reachable on ES only via EXT_texture_format_BGRA8888, and then only with
            // GL_BGRA as the internal format too. Converting instead keeps this correct on desktop
            // GL and ES alike without depending on an extension, at the cost of one pass over the
            // pixels — which previews, bounded by PreviewStreamHelpers' budgets, can afford.
            byte[] rgba = ArrayPool<byte>.Shared.Rent(bgra.Length);
            try
            {
                for (int i = 0; i + 3 < bgra.Length; i += 4)
                {
                    rgba[i] = bgra[i + 2];
                    rgba[i + 1] = bgra[i + 1];
                    rgba[i + 2] = bgra[i];
                    rgba[i + 3] = bgra[i + 3];
                }

                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba,
                    (uint)width,
                    (uint)height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    in rgba[0]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        _gl.BindTexture(TextureTarget.Texture2D, 0);

        var handle = new GlHandle(ContextId, name, GlResourceKind.Texture);
        _registry.Track(handle);
        return handle;
    }

    public GlHandle CreateProgram(string vertexSource, string fragmentSource, out string? linkLog)
    {
        linkLog = null;
        if (ShouldNoOp)
        {
            return GlHandle.Zero(GlResourceKind.Program);
        }

        uint vertexShader = 0;
        uint fragmentShader = 0;
        try
        {
            vertexShader = CompileShader(ShaderType.VertexShader, vertexSource, out string? vertexLog);
            if (vertexShader == 0)
            {
                linkLog = vertexLog;
                return GlHandle.Zero(GlResourceKind.Program);
            }

            fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource, out string? fragmentLog);
            if (fragmentShader == 0)
            {
                linkLog = fragmentLog;
                return GlHandle.Zero(GlResourceKind.Program);
            }

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vertexShader);
            _gl.AttachShader(program, fragmentShader);
            _gl.LinkProgram(program);

            int linkStatus = _gl.GetProgram(program, ProgramPropertyARB.LinkStatus);
            if (linkStatus == 0)
            {
                linkLog = _gl.GetProgramInfoLog(program);
                _gl.DeleteProgram(program);
                return GlHandle.Zero(GlResourceKind.Program);
            }

            var handle = new GlHandle(ContextId, program, GlResourceKind.Program);
            _registry.Track(handle);
            return handle;
        }
        finally
        {
            // Shaders may be deleted right after linking; the program keeps its own copy of the
            // compiled state. This keeps LiveResources limited to buffers/textures/programs that
            // ModelRenderer actually owns and must account for.
            if (vertexShader != 0)
            {
                _gl.DeleteShader(vertexShader);
            }

            if (fragmentShader != 0)
            {
                _gl.DeleteShader(fragmentShader);
            }
        }
    }

    private uint CompileShader(ShaderType type, string source, out string? log)
    {
        log = null;
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        int status = _gl.GetShader(shader, ShaderParameterName.CompileStatus);
        if (status != 0)
        {
            return shader;
        }

        log = _gl.GetShaderInfoLog(shader);
        _gl.DeleteShader(shader);
        return 0;
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

        switch (handle.Kind)
        {
            case GlResourceKind.Buffer:
                _gl.DeleteBuffer(handle.Name);
                break;
            case GlResourceKind.Texture:
                _gl.DeleteTexture(handle.Name);
                break;
            case GlResourceKind.Program:
                _gl.DeleteProgram(handle.Name);
                break;
            case GlResourceKind.Shader:
                _gl.DeleteShader(handle.Name);
                break;
            case GlResourceKind.VertexArray:
                _gl.DeleteVertexArray(handle.Name);
                break;
        }
    }

    public void Clear(int framebuffer, int viewportWidth, int viewportHeight)
    {
        if (ShouldNoOp)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
        if (viewportWidth > 0 && viewportHeight > 0)
        {
            _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
        }

        // Depth writes must be on for glClear to touch the depth buffer at all. Nothing in this
        // device disables them, but clearing is worthless if that ever changes silently.
        _gl.DepthMask(true);

        // Transparent, so the slot's own background shows through around the model rather than the
        // viewport painting a black rectangle over it.
        _gl.ClearColor(0f, 0f, 0f, 0f);

        // The clear depth is deliberately left at GL's default of 1.0 rather than set explicitly.
        // glClearDepth takes a double and exists only in desktop GL; OpenGL ES spells it
        // glClearDepthf. This device runs on whatever the host hands it — ANGLE gives an ES 3.0
        // context on Windows — and Silk resolves entry points lazily through GetProcAddress, so
        // naming an absent one yields a null pointer and calling it takes the process down with an
        // access violation rather than a catchable error. Since the default is already the value
        // wanted here, the safe move is to name neither variant.
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    }

    public unsafe void Draw(in GlDrawCall call)
    {
        if (ShouldNoOp || call.IndexCount <= 0)
        {
            return;
        }

        if (call.Program.Context != ContextId
            || call.VertexBuffer.Context != ContextId
            || call.IndexBuffer.Context != ContextId
            || (call.Texture is { } textureHandle && textureHandle.Context != ContextId))
        {
            // Defense-in-depth: ModelRenderer is responsible for the cross-context guard, but the
            // device never issues a GL call against a handle it did not itself mint either.
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)call.Framebuffer);
        if (call.ViewportWidth > 0 && call.ViewportHeight > 0)
        {
            _gl.Viewport(0, 0, (uint)call.ViewportWidth, (uint)call.ViewportHeight);
        }


        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, call.VertexBuffer.Name);
        _gl.VertexAttribPointer(PositionAttributeLocation, 3, VertexAttribPointerType.Float, false, VertexStrideBytes, (IntPtr)0);
        _gl.EnableVertexAttribArray(PositionAttributeLocation);
        _gl.VertexAttribPointer(NormalAttributeLocation, 3, VertexAttribPointerType.Float, false, VertexStrideBytes, (IntPtr)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(NormalAttributeLocation);
        _gl.VertexAttribPointer(TexCoordAttributeLocation, 2, VertexAttribPointerType.Float, false, VertexStrideBytes, (IntPtr)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(TexCoordAttributeLocation);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, call.IndexBuffer.Name);

        _gl.UseProgram(call.Program.Name);
        SetMatrixUniform(call.Program.Name, "uWorld", call.WorldTransform);
        SetMatrixUniform(call.Program.Name, "uViewProjection", call.ViewProjection);
        SetVector3Uniform(call.Program.Name, "uLightDirection", LightDirection);
        SetVector3Uniform(call.Program.Name, "uAmbientColor", AmbientColor);
        SetVector3Uniform(call.Program.Name, "uDiffuseColor", call.DiffuseColor);
        SetBoolUniform(call.Program.Name, "uAlphaTestEnabled", call.AlphaTestEnabled);
        SetFloatUniform(call.Program.Name, "uAlphaTestThreshold", call.AlphaTestThreshold);

        bool hasTexture = call.Texture is { IsZero: false };
        SetBoolUniform(call.Program.Name, "uHasTexture", hasTexture);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, hasTexture ? call.Texture!.Value.Name : 0);
        SetIntUniform(call.Program.Name, "uDiffuseTexture", 0);

        if (call.AlphaBlendEnabled)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        else
        {
            _gl.Disable(EnableCap.Blend);
        }

        _gl.DrawElements(PrimitiveType.Triangles, (uint)call.IndexCount, DrawElementsType.UnsignedShort, (void*)0);

        _gl.BindVertexArray(0);
        _gl.DeleteVertexArray(vao);
    }

    private void SetMatrixUniform(uint program, string name, in Matrix4x4 matrix)
    {
        int location = _gl.GetUniformLocation(program, name);
        if (location < 0)
        {
            return;
        }

        // Element order and the transpose flag are one decision and live together in
        // GlMatrixUpload, where they can be asserted without a driver. See that type for why an
        // untransposed upload is what GLSL's column-vector convention actually wants.
        ReadOnlySpan<float> values = GlMatrixUpload.Pack(matrix);
        _gl.UniformMatrix4(location, GlMatrixUpload.Transpose, values);
    }

    private void SetVector3Uniform(uint program, string name, Vector3 value)
    {
        int location = _gl.GetUniformLocation(program, name);
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

    private void SetFloatUniform(uint program, string name, float value)
    {
        int location = _gl.GetUniformLocation(program, name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetIntUniform(uint program, string name, int value)
    {
        int location = _gl.GetUniformLocation(program, name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetBoolUniform(uint program, string name, bool value) => SetIntUniform(program, name, value ? 1 : 0);

    public void MarkLost()
    {
        if (_isLost)
        {
            return;
        }

        // The core of architecture decision A4: the driver has already invalidated every name in a
        // lost context, so we only forget — never a single glDelete* call.
        _isLost = true;
        _registry.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Delete while the device still considers itself live, so Delete() actually issues its GL
        // calls; only then flip _disposed so every later call on this device becomes a no-op.
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
