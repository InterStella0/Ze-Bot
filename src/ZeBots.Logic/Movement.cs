namespace ZeBots.Logic;

/// <summary>The server's movement convars (sv_accelerate and friends), with CS2's defaults.</summary>
public sealed record MoveParams
{
    public float Accelerate { get; init; } = 5.5f;
    public float AirAccelerate { get; init; } = 12f;
    public float Friction { get; init; } = 5.2f;
    public float StopSpeed { get; init; } = 80f;
    public float AirMaxWishSpeed { get; init; } = 30f;
}

/// <summary>
/// Player movement as the game does it, for bots whose input cannot be written into the usercmd:
/// instead of pressing a key, the bot's velocity is accelerated toward where it wants to go by the
/// same rules a held key would apply. The bot speeds up and slows down like a player, never
/// exceeds its run speed by its own doing, and keeps whatever else pushes it (ZR knockback, a
/// trigger_push) to wear off through friction as usual.
/// </summary>
public static class Movement
{
    /// <summary>
    /// The velocity to give a bot this tick, before the game's own movement applies friction and
    /// moves it.
    /// </summary>
    /// <param name="velocity">The bot's velocity now.</param>
    /// <param name="wish">Horizontal direction to go; its length (at most 1) scales <paramref name="maxSpeed"/>.</param>
    /// <param name="maxSpeed">Run speed with the bot's weapon.</param>
    public static Vec3 Accelerate(Vec3 velocity, Vec3 wish, float maxSpeed, bool onGround, MoveParams p, float dt)
    {
        var dir = wish.Normalized2D();
        if (dir.IsZero || maxSpeed <= 0f || dt <= 0f) return velocity;
        var wishSpeed = maxSpeed * Math.Clamp(wish.Length2D, 0f, 1f);

        if (!onGround)
        {
            // Air control: only a little speed can be added along the wish direction.
            var airAdd = MathF.Min(wishSpeed, p.AirMaxWishSpeed) - Dot2D(velocity, dir);
            if (airAdd <= 0f) return velocity;
            return velocity + dir * MathF.Min(airAdd, p.AirAccelerate * wishSpeed * dt);
        }

        // On the ground a player falls no faster: the game zeroes their downward speed every tick. A
        // driven bot is taken off the ground for the tick its velocity is set, so the gravity it
        // picked up stays in its velocity; kept, it would build up tick after tick, and each landing
        // on a slope going down would turn it into speed along the slope.
        velocity = Grounded(velocity);

        // On the ground the game applies friction after this, then moves the bot. Apply that friction
        // here first, accelerate as a held key would, then undo it again so the game's own friction
        // lands on the intended result.
        var slowed = Friction(velocity, p, dt);

        var add = wishSpeed - Dot2D(slowed, dir);
        if (add <= 0f) return velocity;
        var accelerated = slowed + dir * MathF.Min(add, p.Accelerate * wishSpeed * dt);

        var target = accelerated.Length2D;
        var before = UndoFriction(target, p, dt);
        var result = target <= 0f ? Vec3.Zero : accelerated * (before / target);
        return new Vec3(result.X, result.Y, velocity.Z);
    }

    /// <summary>
    /// <paramref name="velocity"/> without falling speed, for a bot on the ground. Upward speed stays:
    /// knockback can lift a bot that is still standing.
    /// </summary>
    public static Vec3 Grounded(Vec3 velocity) => velocity.Z < 0f ? velocity with { Z = 0f } : velocity;

    /// <summary>The horizontal velocity one tick of ground friction leaves of <paramref name="velocity"/>.</summary>
    public static Vec3 Friction(Vec3 velocity, MoveParams p, float dt)
    {
        var flat = new Vec3(velocity.X, velocity.Y, 0);
        var speed = flat.Length2D;
        if (speed <= 0f) return Vec3.Zero;
        var after = MathF.Max(0f, speed - MathF.Max(speed, p.StopSpeed) * p.Friction * dt);
        return flat * (after / speed);
    }

    /// <summary>
    /// Takes back speed the bot gained on the ground since ZeBots last set its velocity
    /// (<paramref name="set"/>) that only a held movement key could have added: the stock bot AI
    /// pressing its own keys on top of ZeBots' drive, which at an angle to it can push a bot well
    /// past its run speed. Knockback and pushes add far more than a key can in one tick and are
    /// left alone.
    /// </summary>
    public static Vec3 Govern(Vec3 velocity, Vec3 set, float maxSpeed, MoveParams p, float dt)
    {
        var speed = velocity.Length2D;
        if (speed <= maxSpeed) return velocity;
        var expected = Friction(set, p, dt);
        var gained = speed - expected.Length2D;
        // A held key adds at most sv_accelerate * run speed * dt a tick; a bit more for rounding.
        if (gained <= 0f || gained > 1.5f * p.Accelerate * maxSpeed * dt) return velocity;
        return new Vec3(expected.X, expected.Y, velocity.Z);
    }

    /// <summary>The speed that friction slows to <paramref name="speed"/> in one tick.</summary>
    private static float UndoFriction(float speed, MoveParams p, float dt)
    {
        var k = p.Friction * dt;
        if (speed <= 0f) return 0f;
        if (k >= 1f) return speed;
        var fast = speed / (1f - k);
        return fast >= p.StopSpeed ? fast : speed + p.StopSpeed * k;
    }

    private static float Dot2D(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y;
}
