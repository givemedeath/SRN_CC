using System.Numerics;
using NUnit.Framework;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Preview.Render.Gl;

/// <summary>
/// A matrix uniform must reach the shader meaning what it meant in C#.
/// </summary>
/// <remarks>
/// The upload used to transpose, on the reasoning that <see cref="Matrix4x4"/> is row-major and GLSL
/// is column-major. Both halves are true and the conclusion does not follow: the conventions cancel,
/// and transposing reintroduces the error. Nothing reports it — GL accepts the buffer and the shader
/// runs — so the only symptom was that every model drew wrong, with a translation term feeding
/// <c>w</c> and smearing the scene radially from a point.
/// <para>
/// These compare against <see cref="Vector4.Transform(Vector4, Matrix4x4)"/>, which is the meaning
/// the rest of the render layer computes in, so the assertion is that the GPU agrees with the CPU
/// rather than that the buffer holds particular bytes.
/// </para>
/// </remarks>
[TestFixture]
public class GlMatrixUploadTests
{
    private const float Tolerance = 1e-4f;

    private static void AssertMatchesCpu(Matrix4x4 matrix, Vector3 point)
    {
        Vector4 expected = Vector4.Transform(new Vector4(point, 1f), matrix);
        Vector4 actual = GlMatrixUpload.ApplyAsShaderWould(
            GlMatrixUpload.Pack(matrix), GlMatrixUpload.Transpose, new Vector4(point, 1f));

        Assert.That(actual.X, Is.EqualTo(expected.X).Within(Tolerance));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(Tolerance));
        Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(Tolerance));
        Assert.That(actual.W, Is.EqualTo(expected.W).Within(Tolerance));
    }

    [Test]
    public void ATranslation_MovesThePointRatherThanItsW()
    {
        AssertMatchesCpu(Matrix4x4.CreateTranslation(5f, -3f, 2f), new Vector3(1f, 1f, 1f));
    }

    [Test]
    public void ARotation_AgreesWithTheCpu()
    {
        AssertMatchesCpu(Matrix4x4.CreateRotationZ(0.7f) * Matrix4x4.CreateRotationX(-0.3f), new Vector3(2f, 0.5f, -1f));
    }

    [Test]
    public void APerspectiveProjection_ProducesTheSameClipSpacePoint()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -10f, 4f), Vector3.Zero, Vector3.UnitZ);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 16f / 9f, 0.1f, 500f);

        AssertMatchesCpu(view * projection, new Vector3(1.5f, 2f, -0.5f));
    }

    [Test]
    public void APerspectiveProjection_LeavesWCarryingDepthNotATranslation()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -10f, 0f), Vector3.Zero, Vector3.UnitZ);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1f, 0.1f, 500f);
        Matrix4x4 viewProjection = view * projection;

        Vector4 near = GlMatrixUpload.ApplyAsShaderWould(
            GlMatrixUpload.Pack(viewProjection), GlMatrixUpload.Transpose, new Vector4(0f, -5f, 0f, 1f));
        Vector4 far = GlMatrixUpload.ApplyAsShaderWould(
            GlMatrixUpload.Pack(viewProjection), GlMatrixUpload.Transpose, new Vector4(0f, 5f, 0f, 1f));

        // w is the perspective divisor and must grow with distance from the eye. When the upload was
        // transposed it was driven by a translation term instead, which is what smeared the scene.
        Assert.That(near.W, Is.GreaterThan(0f));
        Assert.That(far.W, Is.GreaterThan(near.W));
    }

    [Test]
    public void TransposingTheUpload_WouldDisagreeWithTheCpu()
    {
        // The guard itself: if this ever stops failing, the test above has stopped proving anything.
        Matrix4x4 matrix = Matrix4x4.CreateTranslation(5f, -3f, 2f);
        Vector4 point = new(1f, 1f, 1f, 1f);

        Vector4 expected = Vector4.Transform(point, matrix);
        Vector4 wrong = GlMatrixUpload.ApplyAsShaderWould(
            GlMatrixUpload.Pack(matrix), !GlMatrixUpload.Transpose, point);

        Assert.That(wrong.W, Is.Not.EqualTo(expected.W).Within(Tolerance));
    }
}
