namespace ZeBots.Logic;

/// <summary>Angle and movement math in Source conventions (yaw 0 = +X, positive pitch looks down).</summary>
public static class Steering
{
    public static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        if (yaw > 180f) yaw -= 360f;
        else if (yaw <= -180f) yaw += 360f;
        return yaw;
    }

    /// <summary>View angles that look from <paramref name="from"/> at <paramref name="to"/>.</summary>
    public static (float Yaw, float Pitch) LookAt(Vec3 from, Vec3 to)
    {
        var delta = to - from;
        var yaw = MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI);
        var pitch = MathF.Atan2(-delta.Z, delta.Length2D) * (180f / MathF.PI);
        return (yaw, pitch);
    }

    /// <summary>Largest of the yaw and pitch differences between two views, in degrees.</summary>
    public static float AngleError(float yaw, float pitch, float targetYaw, float targetPitch) =>
        MathF.Max(MathF.Abs(NormalizeYaw(targetYaw - yaw)), MathF.Abs(targetPitch - pitch));

    /// <summary>Turns the view toward the target, by at most <paramref name="maxDelta"/> degrees per axis.</summary>
    public static (float Yaw, float Pitch) TurnToward(float yaw, float pitch, float targetYaw, float targetPitch, float maxDelta)
    {
        var yawStep = Math.Clamp(NormalizeYaw(targetYaw - yaw), -maxDelta, maxDelta);
        var pitchStep = Math.Clamp(targetPitch - pitch, -maxDelta, maxDelta);
        return (NormalizeYaw(yaw + yawStep), Math.Clamp(pitch + pitchStep, -89f, 89f));
    }

    /// <summary>
    /// Splits a horizontal world direction into the usercmd's forward and left moves (-1..1) for a
    /// player looking along <paramref name="yaw"/>, so a bot can walk one way while aiming another.
    /// </summary>
    public static (float Forward, float Left) ToLocal(Vec3 direction, float yaw)
    {
        var flat = direction.Normalized2D();
        if (flat.IsZero) return (0f, 0f);
        var radians = yaw * (MathF.PI / 180f);
        var (sin, cos) = MathF.SinCos(radians);
        var forward = flat.X * cos + flat.Y * sin;
        var left = -flat.X * sin + flat.Y * cos;
        return (Math.Clamp(forward, -1f, 1f), Math.Clamp(left, -1f, 1f));
    }
}
