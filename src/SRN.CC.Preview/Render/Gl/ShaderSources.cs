namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// Textured-lit shader source, in both a GLES-3.0 variant (ANGLE, the real path on Windows per
/// constraint 4) and a desktop-GL-3.3-core variant. <see cref="SilkGlDevice"/> selects between them
/// using <see cref="GlCapabilities.IsEmbeddedProfile"/>, never a build-time assumption. Both shade a
/// single diffuse texture (or a flat fallback colour) against one fixed headlight directional light
/// plus ambient, with alpha-test discard support. Vertex colour/tint is out of scope (milestone-6
/// MVP: static materials only).
/// </summary>
/// <remarks>
/// Attribute layout is shared by both variants and by <see cref="SilkGlDevice"/>'s vertex buffer
/// packing: location 0 = position (vec3), location 1 = normal (vec3), location 2 = texcoord (vec2).
/// Uniforms are also shared by name: <c>uWorld</c>, <c>uViewProjection</c>, <c>uDiffuseTexture</c>,
/// <c>uHasTexture</c>, <c>uDiffuseColor</c>, <c>uLightDirection</c>, <c>uAmbientColor</c>,
/// <c>uAlphaTestEnabled</c>, <c>uAlphaTestThreshold</c>.
/// </remarks>
internal static class ShaderSources
{
    /// <summary>GLES 3.00 vertex shader (ANGLE / EGL context).</summary>
    public const string VertexSourceGles = """
        #version 300 es

        in vec3 aPosition;
        in vec3 aNormal;
        in vec2 aTexCoord;

        uniform mat4 uWorld;
        uniform mat4 uViewProjection;

        out vec3 vNormal;
        out vec2 vTexCoord;

        void main()
        {
            vNormal = mat3(uWorld) * aNormal;
            vTexCoord = aTexCoord;
            gl_Position = uViewProjection * uWorld * vec4(aPosition, 1.0);
        }
        """;

    /// <summary>GLES 3.00 fragment shader (ANGLE / EGL context).</summary>
    public const string FragmentSourceGles = """
        #version 300 es
        precision mediump float;

        uniform sampler2D uDiffuseTexture;
        uniform bool uHasTexture;
        uniform vec3 uDiffuseColor;
        uniform vec3 uLightDirection;
        uniform vec3 uAmbientColor;
        uniform bool uAlphaTestEnabled;
        uniform float uAlphaTestThreshold;

        in vec3 vNormal;
        in vec2 vTexCoord;

        out vec4 fragColor;

        void main()
        {
            vec4 baseColor = uHasTexture ? texture(uDiffuseTexture, vTexCoord) : vec4(uDiffuseColor, 1.0);
            if (uAlphaTestEnabled && baseColor.a < uAlphaTestThreshold)
            {
                discard;
            }

            vec3 normal = normalize(vNormal);
            float diffuseTerm = max(dot(normal, -uLightDirection), 0.0);
            vec3 lit = baseColor.rgb * (uAmbientColor + vec3(diffuseTerm));
            fragColor = vec4(lit, baseColor.a);
        }
        """;

    /// <summary>Desktop GL 3.30 core vertex shader.</summary>
    public const string VertexSourceDesktop = """
        #version 330 core

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aTexCoord;

        uniform mat4 uWorld;
        uniform mat4 uViewProjection;

        out vec3 vNormal;
        out vec2 vTexCoord;

        void main()
        {
            vNormal = mat3(uWorld) * aNormal;
            vTexCoord = aTexCoord;
            gl_Position = uViewProjection * uWorld * vec4(aPosition, 1.0);
        }
        """;

    /// <summary>Desktop GL 3.30 core fragment shader.</summary>
    public const string FragmentSourceDesktop = """
        #version 330 core

        uniform sampler2D uDiffuseTexture;
        uniform bool uHasTexture;
        uniform vec3 uDiffuseColor;
        uniform vec3 uLightDirection;
        uniform vec3 uAmbientColor;
        uniform bool uAlphaTestEnabled;
        uniform float uAlphaTestThreshold;

        in vec3 vNormal;
        in vec2 vTexCoord;

        out vec4 fragColor;

        void main()
        {
            vec4 baseColor = uHasTexture ? texture(uDiffuseTexture, vTexCoord) : vec4(uDiffuseColor, 1.0);
            if (uAlphaTestEnabled && baseColor.a < uAlphaTestThreshold)
            {
                discard;
            }

            vec3 normal = normalize(vNormal);
            float diffuseTerm = max(dot(normal, -uLightDirection), 0.0);
            vec3 lit = baseColor.rgb * (uAmbientColor + vec3(diffuseTerm));
            fragColor = vec4(lit, baseColor.a);
        }
        """;

    /// <summary>Selects the vertex/fragment source pair for the given probed capabilities.</summary>
    public static (string VertexSource, string FragmentSource) Select(GlCapabilities capabilities)
        => capabilities.IsEmbeddedProfile
            ? (VertexSourceGles, FragmentSourceGles)
            : (VertexSourceDesktop, FragmentSourceDesktop);
}
