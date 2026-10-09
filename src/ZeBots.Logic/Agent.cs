namespace ZeBots.Logic;

/// <summary>ZR moves infected players to T, so the team is the role.</summary>
public enum Role
{
    Human,
    Zombie,
}

/// <summary>
/// A living player as seen in one logic pass.
/// </summary>
/// <param name="Slot">Player slot (entity index - 1), also Bot-Controller's slot.</param>
/// <param name="Yaw">Current view yaw in degrees.</param>
/// <param name="Pitch">Current view pitch in degrees; positive looks down, as in Source.</param>
/// <param name="SpottedBy">
/// The engine's <c>m_bSpottedByMask</c>: bit N is set when the player in slot N can see this one.
/// The engine only tracks enemies, which is exactly what zombies and human bots need.
/// </param>
/// <param name="Ducked">Crouched (or crouching).</param>
/// <param name="OnLadder">Riding a ladder.</param>
/// <param name="Velocity">How fast and which way the player is moving.</param>
/// <param name="Firing">Fired a weapon a moment ago.</param>
public sealed record Agent(
    int Slot,
    bool IsBot,
    Role Role,
    Vec3 Position,
    bool OnGround,
    float Yaw,
    float Pitch,
    ulong SpottedBy,
    bool Ducked = false,
    bool OnLadder = false,
    Vec3 Velocity = default,
    bool Firing = false)
{
    public const float EyeHeight = 64f;
    public const float ChestHeight = 48f;

    public Vec3 Eye => Position + new Vec3(0, 0, EyeHeight);
    public Vec3 Chest => Position + new Vec3(0, 0, ChestHeight);

    /// <summary>Unit vector along the player's view.</summary>
    public Vec3 Forward
    {
        get
        {
            var (sinYaw, cosYaw) = MathF.SinCos(Yaw * (MathF.PI / 180f));
            var (sinPitch, cosPitch) = MathF.SinCos(Pitch * (MathF.PI / 180f));
            return new Vec3(cosPitch * cosYaw, cosPitch * sinYaw, -sinPitch);
        }
    }

    public bool IsSpottedBy(int slot) => slot is >= 0 and < 64 && ((SpottedBy >> slot) & 1) != 0;
}
