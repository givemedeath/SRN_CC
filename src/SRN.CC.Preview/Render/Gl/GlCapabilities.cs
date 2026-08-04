using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Silk.NET.OpenGL;

namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// A snapshot of what the live GL context can actually do, probed once at device construction time.
/// Constraint 4 of the milestone-6 plan: Avalonia on Windows renders through ANGLE, so the context is
/// OpenGL ES via EGL, not desktop GL — this must be treated as a runtime capability probe, never an
/// assumption baked into shader source or draw calls.
/// </summary>
/// <param name="IsEmbeddedProfile">True when <c>GL_VERSION</c> identifies an OpenGL ES context (contains "OpenGL ES").</param>
/// <param name="MajorVersion">Parsed major version number.</param>
/// <param name="MinorVersion">Parsed minor version number.</param>
/// <param name="HasVertexArrayObjects">Whether core VAO support can be assumed for this profile/version.</param>
/// <param name="HasNonPowerOfTwoTextures">Whether full (mip + repeat) NPOT texture support can be assumed.</param>
/// <param name="MaxTextureSize">Value of <c>GL_MAX_TEXTURE_SIZE</c>.</param>
/// <param name="GlslVersionDirective">The <c>#version</c> directive line to prefix shader sources with.</param>
/// <param name="RendererName">Value of <c>GL_RENDERER</c>, for diagnostics.</param>
public sealed record GlCapabilities(
    bool IsEmbeddedProfile,
    int MajorVersion,
    int MinorVersion,
    bool HasVertexArrayObjects,
    bool HasNonPowerOfTwoTextures,
    int MaxTextureSize,
    string GlslVersionDirective,
    string RendererName)
{
    private static readonly Regex VersionNumberPattern = new(@"(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Queries a live <see cref="GL"/> instance for version, renderer, and limit strings and derives
    /// a <see cref="GlCapabilities"/> snapshot. Safe to call from <see cref="SilkGlDevice"/>'s
    /// constructor, immediately after <c>GL.GetApi</c>.
    /// </summary>
    public static unsafe GlCapabilities Probe(GL gl)
    {
        string version = ReadGlString(gl, StringName.Version);
        string renderer = ReadGlString(gl, StringName.Renderer);

        bool isEmbedded = version.Contains("OpenGL ES", StringComparison.Ordinal);

        int major = 1;
        int minor = 0;
        Match match = VersionNumberPattern.Match(version);
        if (match.Success)
        {
            _ = int.TryParse(match.Groups[1].Value, out major);
            _ = int.TryParse(match.Groups[2].Value, out minor);
        }

        int maxTextureSize = gl.GetInteger(GetPName.MaxTextureSize);

        // Core VAO support: desktop GL 3.0+ and GLES 3.0+ both expose VAOs as core, unextended
        // functionality. Older contexts require ARB_vertex_array_object / OES_vertex_array_object,
        // which we do not probe for here — treat them as unsupported and let capability-shortfall
        // degradation (ModelRenderer.Initialize) take over.
        bool hasVao = major >= 3;

        // Full NPOT (mipmapped + repeat-wrap) support is core on desktop GL 2.0+ and GLES 3.0+.
        // GLES 2.0 only allows NPOT with no mipmaps and clamp-to-edge wrap, which is not "full"
        // support for our purposes.
        bool hasNpot = isEmbedded ? major >= 3 : (major > 2 || (major == 2 && minor >= 0));

        string glslDirective = isEmbedded ? "#version 300 es" : "#version 330 core";

        return new GlCapabilities(
            IsEmbeddedProfile: isEmbedded,
            MajorVersion: major,
            MinorVersion: minor,
            HasVertexArrayObjects: hasVao,
            HasNonPowerOfTwoTextures: hasNpot,
            MaxTextureSize: maxTextureSize,
            GlslVersionDirective: glslDirective,
            RendererName: renderer);
    }

    private static unsafe string ReadGlString(GL gl, StringName name)
    {
        byte* raw = gl.GetString(name);
        if (raw is null)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringUTF8((IntPtr)raw) ?? string.Empty;
    }
}
