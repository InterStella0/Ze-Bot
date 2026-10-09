namespace ZeBots.Logic;

/// <summary>A world position or direction in Source units (Z is up).</summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public float Length2D => MathF.Sqrt(X * X + Y * Y);
    public bool IsZero => X == 0 && Y == 0 && Z == 0;

    /// <summary>Horizontal unit vector, or zero when there is no horizontal component.</summary>
    public Vec3 Normalized2D()
    {
        var length = Length2D;
        return length < 1e-4f ? Zero : new Vec3(X / length, Y / length, 0);
    }

    public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;
    public static float Distance2D(Vec3 a, Vec3 b) => (a - b).Length2D;

    /// <summary>
    /// Walking cost between two points: height differences count double, because a point on
    /// another floor is usually much further away by foot than it looks.
    /// </summary>
    public static float WalkCost(Vec3 a, Vec3 b) => Distance2D(a, b) + 2 * MathF.Abs(a.Z - b.Z);
}
