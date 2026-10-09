using ZeBots.Logic;

namespace ZeBots.Logic.Tests;

public class MovementTests
{
    private const float Dt = 1f / 64f;
    private static readonly MoveParams Params = new();
    private static readonly Vec3 East = new(1, 0, 0);

    /// <summary>What the game does after ZeBots each tick: ground friction.</summary>
    private static Vec3 GameFriction(Vec3 velocity)
    {
        var speed = velocity.Length2D;
        if (speed <= 0f) return velocity;
        var slowed = MathF.Max(0f, speed - MathF.Max(speed, Params.StopSpeed) * Params.Friction * Dt);
        return new Vec3(velocity.X * slowed / speed, velocity.Y * slowed / speed, velocity.Z);
    }

    private static Vec3 Tick(Vec3 velocity, Vec3 wish, bool onGround = true)
    {
        var next = Movement.Accelerate(velocity, wish, 250f, onGround, Params, Dt);
        return onGround ? GameFriction(next) : next;
    }

    [Fact]
    public void StartsFromRestLikeAHeldKey()
    {
        var velocity = Tick(Vec3.Zero, East);

        Assert.Equal(Params.Accelerate * 250f * Dt, velocity.X, 1);
        Assert.Equal(0f, velocity.Y, 3);
    }

    [Fact]
    public void ReachesRunSpeedAndNeverPassesIt()
    {
        var velocity = Vec3.Zero;
        for (var i = 0; i < 200; i++)
        {
            velocity = Tick(velocity, East);
            Assert.True(velocity.Length2D <= 250.01f, $"tick {i}: {velocity.Length2D}");
        }
        Assert.Equal(250f, velocity.Length2D, 0);
    }

    [Fact]
    public void ScalesWithTheWishLength()
    {
        var velocity = Vec3.Zero;
        for (var i = 0; i < 200; i++) velocity = Tick(velocity, East * 0.5f);
        Assert.Equal(125f, velocity.Length2D, 0);
    }

    [Fact]
    public void FallingSpeedDoesNotBuildUpOnTheGround()
    {
        // Running down a slope: gravity left in the velocity every tick the drive takes the bot off
        // the ground. Kept, it would grow tick after tick and turn into speed down the slope.
        var velocity = new Vec3(250, 0, -120);
        for (var i = 0; i < 64; i++)
        {
            velocity = Tick(velocity, East);
            Assert.Equal(0f, velocity.Z);
            velocity = velocity with { Z = velocity.Z - 800f * Dt };
        }
        Assert.True(velocity.Length2D <= 250.01f, $"{velocity.Length2D}");

        // Over run speed, the drive leaves the horizontal speed alone but still drops the fall.
        Assert.Equal(0f, Movement.Accelerate(new Vec3(400, 0, -50), East, 250f, true, Params, Dt).Z);
    }

    [Fact]
    public void KnockbackStillLiftsAndFallingInTheAirIsKept()
    {
        Assert.Equal(180f, Movement.Accelerate(new Vec3(0, 0, 180), East, 250f, true, Params, Dt).Z);
        Assert.Equal(-300f, Movement.Accelerate(new Vec3(100, 0, -300), East, 250f, false, Params, Dt).Z);
    }

    [Fact]
    public void KnockbackWearsOffInsteadOfBeingOverwritten()
    {
        // Shot back at 600 u/s while running east.
        var velocity = Tick(new Vec3(-600, 0, 0), East);

        // One tick later it is still flying back, slowed only by friction and the key held east.
        Assert.InRange(velocity.X, -600f * (1f - Params.Friction * Dt), -500f);

        var ticks = 0;
        while (velocity.X < 0 && ticks < 200)
        {
            velocity = Tick(velocity, East);
            ticks++;
        }
        // Recovers over several ticks, as a player holding the key does, not at once.
        Assert.InRange(ticks, 8, 40);
    }

    [Fact]
    public void AirControlIsLimited()
    {
        var velocity = new Vec3(250, 0, 100);
        for (var i = 0; i < 64; i++) velocity = Tick(velocity, new Vec3(0, 1, 0), onGround: false);

        // Gains at most the air wish speed sideways and keeps its forward speed.
        Assert.InRange(velocity.Y, 1f, Params.AirMaxWishSpeed + 0.01f);
        Assert.Equal(250f, velocity.X, 1);
    }

    [Fact]
    public void NoWishLeavesTheVelocityAlone()
    {
        var velocity = new Vec3(120, 40, 5);
        Assert.Equal(velocity, Movement.Accelerate(velocity, Vec3.Zero, 250f, onGround: true, Params, Dt));
    }

    /// <summary>
    /// The stock bot AI pressing its own key at right angles to where ZeBots drives: the game adds
    /// a held key's acceleration on top of ZeBots' velocity every tick.
    /// </summary>
    private static float RunWithStockKey(bool governed)
    {
        var north = new Vec3(0, 1, 0);
        var velocity = Vec3.Zero;
        Vec3? set = null;
        for (var i = 0; i < 400; i++)
        {
            if (governed && set is { } last) velocity = Movement.Govern(velocity, last, 250f, Params, Dt);
            var next = Movement.Accelerate(velocity, East, 250f, true, Params, Dt);
            set = next;
            velocity = GameFriction(next);
            // The stock key: up to run speed along it, as the game's own acceleration would.
            var along = velocity.Y;
            velocity += north * MathF.Min(MathF.Max(0f, 250f - along), Params.Accelerate * 250f * Dt);
        }
        return velocity.Length2D;
    }

    [Fact]
    public void TheStockAIsKeysOnTopOfTheDriveWouldMakeTheBotFaster()
    {
        Assert.True(RunWithStockKey(governed: false) > 330f);
    }

    [Fact]
    public void GoverningTakesBackWhatTheStockAIsKeysAdd()
    {
        Assert.InRange(RunWithStockKey(governed: true), 240f, 275f);
    }

    [Fact]
    public void GoverningLeavesKnockbackAlone()
    {
        var set = new Vec3(250, 0, 0);
        var knocked = GameFriction(set) + new Vec3(-400, 0, 0);
        Assert.Equal(knocked, Movement.Govern(knocked, set, 250f, Params, Dt));

        // Already fast from a push and slowing down: nothing to take back either.
        var pushed = new Vec3(600, 0, 0);
        var slowing = GameFriction(pushed);
        Assert.Equal(slowing, Movement.Govern(slowing, pushed, 250f, Params, Dt));
    }
}
