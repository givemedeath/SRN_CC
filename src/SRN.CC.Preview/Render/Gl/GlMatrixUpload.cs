using System.Numerics;

namespace SRN.CC.Preview.Render.Gl;

/// <summary>
/// How a <see cref="Matrix4x4"/> is handed to a GL uniform: the element order written into the
/// buffer, and whether GL is asked to transpose it on the way in.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="SilkGlDevice"/> so the convention can be asserted without a driver.
/// Getting it wrong is silent — GL accepts the upload, the shader runs, and the picture is merely
/// wrong — and the previous mistake here (transposing) fed a translation term into <c>w</c>, so the
/// perspective divide smeared every model radially from a point while still vaguely resembling
/// geometry.
/// </para>
/// <para>
/// The two conventions in play cancel, which is why nothing needs transposing.
/// <see cref="Matrix4x4"/> is row-vector — it computes <c>v * M</c>, so a translation lives in the
/// fourth row — and its memory is row-major. GLSL is column-vector, computing <c>M * v</c> with a
/// translation in the fourth column, and reads uniform data column-major. Those are the same
/// disagreement twice: the row-major bytes of a row-vector matrix are, element for element, the
/// column-major bytes of the equivalent column-vector matrix.
/// </para>
/// </remarks>
internal static class GlMatrixUpload
{
    /// <summary>
    /// What to pass as <c>glUniformMatrix4fv</c>'s <c>transpose</c> flag for a buffer produced by
    /// <see cref="Pack"/>. False: see the type remarks for why the conventions already agree.
    /// </summary>
    internal const bool Transpose = false;

    /// <summary>Writes <paramref name="matrix"/> in <see cref="Matrix4x4"/>'s own row-major order.</summary>
    internal static float[] Pack(in Matrix4x4 matrix) =>
    [
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44,
    ];

    /// <summary>
    /// Applies a packed uniform to <paramref name="point"/> exactly as a GLSL <c>mat4 * vec4</c>
    /// would, so a test can check the upload contract end to end without a GL context.
    /// </summary>
    /// <param name="packed">A buffer from <see cref="Pack"/>.</param>
    /// <param name="transpose">The flag that accompanied it.</param>
    /// <param name="point">The point the vertex shader would transform.</param>
    internal static Vector4 ApplyAsShaderWould(float[] packed, bool transpose, Vector4 point)
    {
        ArgumentNullException.ThrowIfNull(packed);
        if (packed.Length != 16)
        {
            throw new ArgumentException("A mat4 upload is exactly sixteen floats.", nameof(packed));
        }

        // GL reads the buffer column-major unless asked to transpose, so element [column, row] is
        // at column * 4 + row — or row * 4 + column once transposed.
        float Element(int row, int column) =>
            transpose ? packed[(row * 4) + column] : packed[(column * 4) + row];

        Span<float> input = [point.X, point.Y, point.Z, point.W];
        Span<float> output = stackalloc float[4];

        // GLSL's mat4 * vec4: output_row = sum over columns of element[row, column] * input[column].
        for (int row = 0; row < 4; row++)
        {
            float sum = 0f;
            for (int column = 0; column < 4; column++)
            {
                sum += Element(row, column) * input[column];
            }

            output[row] = sum;
        }

        return new Vector4(output[0], output[1], output[2], output[3]);
    }
}
