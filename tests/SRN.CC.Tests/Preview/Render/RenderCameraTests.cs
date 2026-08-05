using System.Numerics;
using NUnit.Framework;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.Preview.Render;

[TestFixture]
public class RenderCameraTests
{
    private const float Tolerance = 1e-4f;

    // ---------------------------------------------------------------------
    // Frame()
    // ---------------------------------------------------------------------

    [Test]
    public void Frame_TargetsBoundsMidpoint()
    {
        Vector3 min = new Vector3(-2f, 0f, -1f);
        Vector3 max = new Vector3(4f, 6f, 3f);

        RenderCamera camera = RenderCamera.Frame(min, max, radius: 5f);

        Vector3 expectedCenter = (min + max) * 0.5f;
        Assert.That(camera.Target.X, Is.EqualTo(expectedCenter.X).Within(Tolerance));
        Assert.That(camera.Target.Y, Is.EqualTo(expectedCenter.Y).Within(Tolerance));
        Assert.That(camera.Target.Z, Is.EqualTo(expectedCenter.Z).Within(Tolerance));
    }

    [TestCase(1f)]
    [TestCase(5f)]
    [TestCase(50f)]
    [TestCase(0.25f)]
    public void Frame_DistanceFitsFullBoundingSphereWithinVerticalFov(float radius)
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-radius), new Vector3(radius), radius);

        // Half-height of the view frustum at the camera's distance must reach at least the
        // framed radius, i.e. the whole bounding sphere fits within the vertical FOV.
        float halfHeightAtDistance = camera.Distance * MathF.Tan(camera.FieldOfViewRadians * 0.5f);

        Assert.That(halfHeightAtDistance, Is.GreaterThanOrEqualTo(radius),
            "the framed distance must place the whole bounding sphere within the vertical FOV");
        Assert.That(camera.Distance, Is.GreaterThan(0f));
    }

    [Test]
    public void Frame_ProducesPositiveNearFarWithNearLessThanFar()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-10f), new Vector3(10f), radius: 17.3f);

        Assert.That(camera.NearPlane, Is.GreaterThan(0f));
        Assert.That(camera.FarPlane, Is.GreaterThan(camera.NearPlane));
        Assert.That(camera.FieldOfViewRadians, Is.GreaterThan(0f));
        Assert.That(camera.FieldOfViewRadians, Is.LessThan(MathF.PI));
    }

    [Test]
    public void Frame_ToleratesZeroRadius_WithoutProducingDegenerateDistance()
    {
        RenderCamera camera = RenderCamera.Frame(Vector3.Zero, Vector3.Zero, radius: 0f);

        Assert.That(camera.Distance, Is.GreaterThan(0f));
        Assert.That(float.IsFinite(camera.Distance), Is.True);
        Assert.That(float.IsFinite(camera.NearPlane), Is.True);
        Assert.That(float.IsFinite(camera.FarPlane), Is.True);
    }

    [TestCase(1f, 10f)]
    [TestCase(10f, 1f)]
    public void Frame_LargerRadius_ProducesLargerDistance(float radiusA, float radiusB)
    {
        RenderCamera cameraA = RenderCamera.Frame(new Vector3(-radiusA), new Vector3(radiusA), radiusA);
        RenderCamera cameraB = RenderCamera.Frame(new Vector3(-radiusB), new Vector3(radiusB), radiusB);

        if (radiusA < radiusB)
        {
            Assert.That(cameraA.Distance, Is.LessThan(cameraB.Distance));
        }
        else
        {
            Assert.That(cameraA.Distance, Is.GreaterThan(cameraB.Distance));
        }
    }

    // ---------------------------------------------------------------------
    // Orbit() pitch clamping
    // ---------------------------------------------------------------------

    [Test]
    public void Orbit_RepeatedLargePositivePitch_ClampsAtMaxPitchAndNeverFlips()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        for (int i = 0; i < 50; i++)
        {
            camera = camera.Orbit(deltaYaw: 0f, deltaPitch: 5f); // wildly overshoots +90 degrees
        }

        Assert.That(camera.Pitch, Is.LessThanOrEqualTo(RenderCamera.MaxPitchRadians));
        Assert.That(camera.Pitch, Is.EqualTo(RenderCamera.MaxPitchRadians).Within(Tolerance));
    }

    [Test]
    public void Orbit_RepeatedLargeNegativePitch_ClampsAtMinPitchAndNeverFlips()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        for (int i = 0; i < 50; i++)
        {
            camera = camera.Orbit(deltaYaw: 0f, deltaPitch: -5f); // wildly overshoots -90 degrees
        }

        Assert.That(camera.Pitch, Is.GreaterThanOrEqualTo(-RenderCamera.MaxPitchRadians));
        Assert.That(camera.Pitch, Is.EqualTo(-RenderCamera.MaxPitchRadians).Within(Tolerance));
    }

    [Test]
    public void Orbit_ApproachingPole_EyeYComponentNeverExceedsDistance()
    {
        // If pitch ever reached/exceeded +/-90 degrees the eye's up-axis (Y) offset would equal or
        // exceed Distance, and the look-at "forward" direction would become parallel to world up
        // (the gimbal-flip condition View relies on never happening).
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        for (int i = 0; i < 200; i++)
        {
            camera = camera.Orbit(deltaYaw: 0.3f, deltaPitch: 10f);
            Vector3 eye = ExtractEyePosition(camera);
            float eyeUpOffset = MathF.Abs(eye.Y - camera.Target.Y);

            Assert.That(eyeUpOffset, Is.LessThan(camera.Distance),
                "eye offset along world-up must stay strictly below distance to avoid a degenerate look-at basis");
        }
    }

    [Test]
    public void Orbit_Yaw_StaysWithinWrappedRange()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        for (int i = 0; i < 100; i++)
        {
            camera = camera.Orbit(deltaYaw: 1.7f, deltaPitch: 0f);
        }

        Assert.That(camera.Yaw, Is.GreaterThanOrEqualTo(-MathF.PI));
        Assert.That(camera.Yaw, Is.LessThanOrEqualTo(MathF.PI));
    }

    [Test]
    public void Orbit_DoesNotMutateOriginalCamera()
    {
        RenderCamera original = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);
        float originalYaw = original.Yaw;
        float originalPitch = original.Pitch;

        _ = original.Orbit(1f, 1f);

        Assert.That(original.Yaw, Is.EqualTo(originalYaw));
        Assert.That(original.Pitch, Is.EqualTo(originalPitch));
    }

    // ---------------------------------------------------------------------
    // Dolly() clamping
    // ---------------------------------------------------------------------

    [Test]
    public void Dolly_RepeatedZoomIn_NeverReachesZeroOrNegative()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        for (int i = 0; i < 100; i++)
        {
            camera = camera.Dolly(0.01f); // aggressively zoom in every step
            Assert.That(camera.Distance, Is.GreaterThan(0f));
        }
    }

    [Test]
    public void Dolly_ExtremeZoomIn_ClampsToPositiveMinimum()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        camera = camera.Dolly(0f);

        Assert.That(camera.Distance, Is.GreaterThan(0f));
        Assert.That(float.IsFinite(camera.Distance), Is.True);
    }

    [Test]
    public void Dolly_RepeatedZoomOut_NeverExceedsSaneMaximum()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);
        float? clampedDistance = null;

        for (int i = 0; i < 100; i++)
        {
            camera = camera.Dolly(10f); // aggressively zoom out every step
            Assert.That(float.IsFinite(camera.Distance), Is.True);

            if (clampedDistance is null && i > 50)
            {
                clampedDistance = camera.Distance;
            }
            else if (clampedDistance is not null)
            {
                // once clamped, further zoom-out must not keep growing the distance
                Assert.That(camera.Distance, Is.EqualTo(clampedDistance.Value).Within(Tolerance));
            }
        }
    }

    [Test]
    public void Dolly_NegativeFactor_StillProducesPositiveClampedDistance()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        camera = camera.Dolly(-5f);

        Assert.That(camera.Distance, Is.GreaterThan(0f));
    }

    [Test]
    public void Dolly_DoesNotMutateOriginalCamera()
    {
        RenderCamera original = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);
        float originalDistance = original.Distance;

        _ = original.Dolly(3f);

        Assert.That(original.Distance, Is.EqualTo(originalDistance));
    }

    // ---------------------------------------------------------------------
    // Pan()
    // ---------------------------------------------------------------------

    [Test]
    public void Pan_TranslatesTargetByWorldDelta()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);
        Vector3 delta = new Vector3(3f, -2f, 0.5f);

        RenderCamera panned = camera.Pan(delta);

        Assert.That(panned.Target.X, Is.EqualTo(camera.Target.X + delta.X).Within(Tolerance));
        Assert.That(panned.Target.Y, Is.EqualTo(camera.Target.Y + delta.Y).Within(Tolerance));
        Assert.That(panned.Target.Z, Is.EqualTo(camera.Target.Z + delta.Z).Within(Tolerance));
    }

    [Test]
    public void Pan_LeavesDistanceYawPitchUnchanged()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        RenderCamera panned = camera.Pan(new Vector3(1f, 1f, 1f));

        Assert.That(panned.Distance, Is.EqualTo(camera.Distance));
        Assert.That(panned.Yaw, Is.EqualTo(camera.Yaw));
        Assert.That(panned.Pitch, Is.EqualTo(camera.Pitch));
    }

    // ---------------------------------------------------------------------
    // View / Projection matrix validity
    // ---------------------------------------------------------------------

    [Test]
    public void View_BasisVectorsAreUnitLength()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f)
            .Orbit(deltaYaw: 0.9f, deltaPitch: 0.4f);

        Matrix4x4 view = camera.View;
        (Vector3 right, Vector3 up, Vector3 forward) = ExtractBasis(view);

        Assert.That(right.Length(), Is.EqualTo(1f).Within(1e-3f));
        Assert.That(up.Length(), Is.EqualTo(1f).Within(1e-3f));
        Assert.That(forward.Length(), Is.EqualTo(1f).Within(1e-3f));
    }

    [Test]
    public void View_BasisVectorsAreMutuallyOrthogonal()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f)
            .Orbit(deltaYaw: -1.2f, deltaPitch: -0.6f);

        Matrix4x4 view = camera.View;
        (Vector3 right, Vector3 up, Vector3 forward) = ExtractBasis(view);

        Assert.That(Vector3.Dot(right, up), Is.EqualTo(0f).Within(1e-3f));
        Assert.That(Vector3.Dot(right, forward), Is.EqualTo(0f).Within(1e-3f));
        Assert.That(Vector3.Dot(up, forward), Is.EqualTo(0f).Within(1e-3f));
    }

    [TestCase(0f, 0f)]
    [TestCase(1.4f, 0.5f)]
    [TestCase(-2.0f, 0.7f)]
    [TestCase(0.1f, -RenderCamera.MaxPitchRadians + 0.001f)]
    public void View_IsOrthonormalAcrossOrbitAngles(float yaw, float pitch)
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f) with
        {
            Yaw = yaw,
            Pitch = pitch,
        };

        Matrix4x4 view = camera.View;
        (Vector3 right, Vector3 up, Vector3 forward) = ExtractBasis(view);

        Assert.That(right.Length(), Is.EqualTo(1f).Within(1e-3f));
        Assert.That(up.Length(), Is.EqualTo(1f).Within(1e-3f));
        Assert.That(forward.Length(), Is.EqualTo(1f).Within(1e-3f));
        Assert.That(Vector3.Dot(right, up), Is.EqualTo(0f).Within(1e-3f));
        Assert.That(Vector3.Dot(right, forward), Is.EqualTo(0f).Within(1e-3f));
        Assert.That(Vector3.Dot(up, forward), Is.EqualTo(0f).Within(1e-3f));
    }

    [Test]
    public void View_LooksAtTarget()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        Matrix4x4 view = camera.View;
        Assert.That(Matrix4x4.Invert(view, out Matrix4x4 inverseView), Is.True, "view matrix must be invertible");

        Vector3 eyeFromView = Vector3.Transform(Vector3.Zero, inverseView);
        Vector3 expectedEye = ExtractEyePosition(camera);

        Assert.That(eyeFromView.X, Is.EqualTo(expectedEye.X).Within(1e-2f));
        Assert.That(eyeFromView.Y, Is.EqualTo(expectedEye.Y).Within(1e-2f));
        Assert.That(eyeFromView.Z, Is.EqualTo(expectedEye.Z).Within(1e-2f));
    }

    [Test]
    public void Projection_MatchesStandardPerspectiveFieldOfViewConstruction()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);
        const float aspect = 16f / 9f;

        Matrix4x4 actual = camera.Projection(aspect);
        Matrix4x4 expected = Matrix4x4.CreatePerspectiveFieldOfView(
            camera.FieldOfViewRadians, aspect, camera.NearPlane, camera.FarPlane);

        Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(Tolerance));
        Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(Tolerance));
        Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(Tolerance));
        Assert.That(actual.M34, Is.EqualTo(expected.M34).Within(Tolerance));
        Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(Tolerance));
        Assert.That(actual.M44, Is.EqualTo(expected.M44).Within(Tolerance));
    }

    [Test]
    public void Projection_HasExpectedPerspectiveStructure()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);
        Matrix4x4 projection = camera.Projection(aspect: 1.5f);

        // Diagonal scale terms strictly positive.
        Assert.That(projection.M11, Is.GreaterThan(0f));
        Assert.That(projection.M22, Is.GreaterThan(0f));

        // Perspective-divide row: M44 is zero and M34 (or M43, depending on convention) is nonzero,
        // which is what turns w into -z/+z for the divide.
        Assert.That(projection.M44, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(MathF.Abs(projection.M34), Is.GreaterThan(0f));

        // Wider aspect ratio must produce a smaller horizontal scale for the same vertical FOV.
        Matrix4x4 wider = camera.Projection(aspect: 3.0f);
        Assert.That(wider.M11, Is.LessThan(projection.M11));
        Assert.That(wider.M22, Is.EqualTo(projection.M22).Within(Tolerance));
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static Vector3 ExtractEyePosition(RenderCamera camera)
    {
        Matrix4x4 view = camera.View;
        Matrix4x4.Invert(view, out Matrix4x4 inverseView);
        return Vector3.Transform(Vector3.Zero, inverseView);
    }

    private static (Vector3 Right, Vector3 Up, Vector3 Forward) ExtractBasis(Matrix4x4 view)
    {
        Vector3 right = new Vector3(view.M11, view.M21, view.M31);
        Vector3 up = new Vector3(view.M12, view.M22, view.M32);
        Vector3 forward = new Vector3(view.M13, view.M23, view.M33);
        return (right, up, forward);
    }

    [Test]
    public void Frame_DeclaredRadiusSmallerThanTheBounds_UsesTheBounds()
    {
        // A stock NWN asteroid model declares 449.6 while its bounds need roughly 501. Trusting the
        // declared value seats the camera inside the model's own extent, so it opens partly outside
        // the view.
        Vector3 min = new(-305f, -302f, -266f);
        Vector3 max = new(305f, 308f, 244f);
        float boundsRadius = (max - min).Length() * 0.5f;

        RenderCamera tight = RenderCamera.Frame(min, max, radius: 449.6f);
        RenderCamera honest = RenderCamera.Frame(min, max, boundsRadius);

        // Framing must cover the geometry even when the model under-declares its radius.
        Assert.That(tight.Distance, Is.EqualTo(honest.Distance).Within(Tolerance));
        Assert.That(tight.Distance, Is.GreaterThan(boundsRadius));
    }

    [Test]
    public void Frame_DeclaredRadiusLargerThanTheBounds_KeepsTheDeclaredRadius()
    {
        Vector3 min = new(-1f);
        Vector3 max = new(1f);

        RenderCamera camera = RenderCamera.Frame(min, max, radius: 50f);
        RenderCamera fromBoundsOnly = RenderCamera.Frame(min, max, radius: 0f);

        // A model declaring more room than its bounds occupy keeps that room.
        Assert.That(camera.Distance, Is.GreaterThan(fromBoundsOnly.Distance));
    }

    // ---------------------------------------------------------------------
    // Up-axis convention (Z-up, matching how Neverwinter Nights authors models)
    // ---------------------------------------------------------------------

    private static Vector3 EyePosition(RenderCamera camera)
    {
        Assert.That(Matrix4x4.Invert(camera.View, out Matrix4x4 inverse), Is.True);
        return inverse.Translation;
    }

    [Test]
    public void View_PutsTheEyeAboveTheTargetInZ_WhenPitched()
    {
        RenderCamera level = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f) with { Pitch = 0f };
        RenderCamera raised = level with { Pitch = 0.7f };

        // Pitch is elevation, and elevation is Z for these models.
        Assert.That(EyePosition(raised).Z, Is.GreaterThan(EyePosition(level).Z));
    }

    [Test]
    public void View_YawSweepKeepsTheEyeAtOneHeight()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f) with { Pitch = 0.4f };

        float height = EyePosition(camera).Z;
        foreach (float yaw in new[] { 0.5f, 1.5f, 3f, 4.5f })
        {
            // Yaw must sweep around the models' vertical axis, not tumble the model over.
            Assert.That(EyePosition(camera with { Yaw = yaw }).Z, Is.EqualTo(height).Within(Tolerance));
        }
    }

    [Test]
    public void View_MapsTheModelsUpAxisToScreenUp()
    {
        RenderCamera camera = RenderCamera.Frame(new Vector3(-1f), new Vector3(1f), 1f);

        Vector3 target = Vector3.Transform(camera.Target, camera.View);
        Vector3 above = Vector3.Transform(camera.Target + RenderCamera.UpAxis, camera.View);

        // A point higher up the model must land higher up the screen: +Y in view space.
        Assert.That(above.Y, Is.GreaterThan(target.Y));
    }

    [Test]
    public void UpAxis_IsZ()
    {
        Assert.That(RenderCamera.UpAxis, Is.EqualTo(Vector3.UnitZ));
    }
}
