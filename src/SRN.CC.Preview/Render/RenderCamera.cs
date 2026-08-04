using System.Numerics;

namespace SRN.CC.Preview.Render;

/// <summary>
/// Immutable orbit camera. Pure struct math with no GPU or UI dependency, so it is fully testable
/// headlessly — this is the majority of the "camera controls" gate evidence (milestone-6 plan).
/// </summary>
/// <remarks>
/// <see cref="Yaw"/>/<see cref="Pitch"/> orbit <see cref="Target"/> at <see cref="Distance"/>; the
/// eye position is derived spherically from those three values, with world-up assumed to be
/// <see cref="Vector3.UnitY"/>.
/// </remarks>
public readonly record struct RenderCamera(
    Vector3 Target,
    float Distance,
    float Yaw,
    float Pitch,
    float FieldOfViewRadians,
    float NearPlane,
    float FarPlane)
{
    /// <summary>Default vertical field of view used by <see cref="Frame"/>: 45 degrees.</summary>
    public const float DefaultFieldOfViewRadians = MathF.PI / 4f;

    private const float DefaultYaw = 0f;

    /// <summary>~20 degrees: a pleasant three-quarter default pitch, never at the poles.</summary>
    private const float DefaultPitch = 0.35f;

    /// <summary>Multiplier applied to the bounds-derived distance so the model does not touch the frustum edges.</summary>
    private const float FrameMargin = 1.15f;

    private const float MinimumRadius = 0.01f;
    private const float MinimumNearPlane = 0.01f;

    /// <summary>Never zero or negative; also the floor for <see cref="Dolly"/>.</summary>
    private const float MinimumDistance = 0.05f;

    /// <summary>Sane ceiling for <see cref="Dolly"/> so repeated zoom-out cannot run away toward float overflow.</summary>
    private const float MaximumDistance = 1_000_000f;

    /// <summary>Just short of +/-90 degrees, so the look-at up vector never degenerates (gimbal-flip guard).</summary>
    public const float MaxPitchRadians = 89f * MathF.PI / 180f;

    /// <summary>
    /// Auto-frames a camera so the whole bounds fit the default field of view: the target is the
    /// bounds midpoint, and distance is derived from <paramref name="radius"/> (the bounding
    /// sphere around that midpoint) so the sphere fits inside the vertical FOV with margin to
    /// spare.
    /// </summary>
    public static RenderCamera Frame(Vector3 boundsMin, Vector3 boundsMax, float radius)
    {
        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        float safeRadius = MathF.Max(radius, MinimumRadius);
        float fov = DefaultFieldOfViewRadians;
        float halfFov = fov * 0.5f;

        // Distance at which a sphere of safeRadius exactly fills the vertical FOV, plus margin.
        float distance = safeRadius / MathF.Sin(halfFov) * FrameMargin;
        distance = Math.Clamp(distance, MinimumDistance, MaximumDistance);

        float near = MathF.Max(safeRadius * 0.01f, MinimumNearPlane);
        float far = MathF.Max((distance + safeRadius) * 4f, near + 1f);

        return new RenderCamera(center, distance, DefaultYaw, DefaultPitch, fov, near, far);
    }

    /// <summary>Standard look-at view matrix; eye orbits <see cref="Target"/> by <see cref="Yaw"/>/<see cref="Pitch"/>/<see cref="Distance"/>.</summary>
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Target + EyeOffset, Target, Vector3.UnitY);

    /// <summary>Standard perspective field-of-view projection matrix.</summary>
    public Matrix4x4 Projection(float aspect)
        => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewRadians, aspect, NearPlane, FarPlane);

    /// <summary>Returns a new camera with yaw/pitch adjusted; pitch is clamped short of the poles to avoid gimbal flip.</summary>
    public RenderCamera Orbit(float deltaYaw, float deltaPitch)
    {
        float newYaw = WrapAngle(Yaw + deltaYaw);
        float newPitch = Math.Clamp(Pitch + deltaPitch, -MaxPitchRadians, MaxPitchRadians);
        return this with { Yaw = newYaw, Pitch = newPitch };
    }

    /// <summary>Returns a new camera with distance scaled by <paramref name="factor"/>, clamped to a sane, always-positive range.</summary>
    public RenderCamera Dolly(float factor)
    {
        float newDistance = Math.Clamp(Distance * factor, MinimumDistance, MaximumDistance);
        return this with { Distance = newDistance };
    }

    /// <summary>Returns a new camera with the orbit target translated in world space.</summary>
    public RenderCamera Pan(Vector3 worldDelta) => this with { Target = Target + worldDelta };

    private Vector3 EyeOffset
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            float x = Distance * cosPitch * MathF.Sin(Yaw);
            float y = Distance * MathF.Sin(Pitch);
            float z = Distance * cosPitch * MathF.Cos(Yaw);
            return new Vector3(x, y, z);
        }
    }

    private static float WrapAngle(float radians)
    {
        const float twoPi = MathF.PI * 2f;
        float wrapped = radians % twoPi;
        if (wrapped > MathF.PI) wrapped -= twoPi;
        else if (wrapped < -MathF.PI) wrapped += twoPi;
        return wrapped;
    }
}
