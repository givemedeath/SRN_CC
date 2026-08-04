using System.Numerics;

namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// Everything <see cref="IGlDevice.Draw"/> needs to issue one indexed draw call. <see cref="ModelRenderer"/>
/// builds one of these per uploaded <c>RenderMesh</c> each frame; the device is the only layer that
/// touches actual GL state (bind buffers, set attribute pointers, bind the texture, set uniforms,
/// issue <c>glDrawElements</c>), so this struct is the full contract between them.
/// </summary>
/// <param name="Program">The linked shader program to use for this draw.</param>
/// <param name="VertexBuffer">
/// Interleaved vertex buffer: position (3 floats) + normal (3 floats) + texcoord (2 floats) per
/// vertex, stride 32 bytes, in that attribute order (locations 0, 1, 2).
/// </param>
/// <param name="IndexBuffer">Index buffer of <c>ushort</c> indices (constraint 6).</param>
/// <param name="IndexCount">Number of indices to draw (3 * triangle count).</param>
/// <param name="Texture">The diffuse texture to bind, or null when the material is untextured.</param>
/// <param name="WorldTransform">The mesh's rest-pose world transform.</param>
/// <param name="ViewProjection">The camera's combined view * projection matrix for this frame.</param>
/// <param name="DiffuseColor">Flat fallback/tint colour, used directly when <paramref name="Texture"/> is null.</param>
/// <param name="AlphaTestEnabled">Whether the fragment shader should discard below <paramref name="AlphaTestThreshold"/>.</param>
/// <param name="AlphaTestThreshold">Cutout threshold in [0, 1], meaningful only when <paramref name="AlphaTestEnabled"/>.</param>
/// <param name="AlphaBlendEnabled">Whether this draw should be issued with blending enabled (back-to-front pass).</param>
/// <param name="Framebuffer">
/// The target framebuffer object name (0 = the default/window framebuffer). Carried per draw call
/// rather than as separate device state, since <see cref="IGlDevice"/> exposes no frame-scoped
/// "begin frame" method — <c>ModelRenderer.Render</c> stamps the same value onto every call in a frame.
/// </param>
/// <param name="ViewportWidth">Viewport width in pixels.</param>
/// <param name="ViewportHeight">Viewport height in pixels.</param>
public readonly record struct GlDrawCall(
    GlHandle Program,
    GlHandle VertexBuffer,
    GlHandle IndexBuffer,
    int IndexCount,
    GlHandle? Texture,
    Matrix4x4 WorldTransform,
    Matrix4x4 ViewProjection,
    Vector3 DiffuseColor,
    bool AlphaTestEnabled,
    float AlphaTestThreshold,
    bool AlphaBlendEnabled,
    int Framebuffer,
    int ViewportWidth,
    int ViewportHeight);
