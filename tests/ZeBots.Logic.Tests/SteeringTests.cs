using ZeBots.Logic;

namespace ZeBots.Logic.Tests;

public class SteeringTests
{
    [Theory]
    [InlineData(190f, -170f)]
    [InlineData(-190f, 170f)]
    [InlineData(540f, 180f)]
    [InlineData(45f, 45f)]
    public void NormalizesYaw(float yaw, float expected) => Assert.Equal(expected, Steering.NormalizeYaw(yaw), 3);

    [Fact]
    public void LooksAtAPointBelowWithPositivePitch()
    {
        var (yaw, pitch) = Steering.LookAt(new Vec3(0, 0, 100), new Vec3(0, 100, 0));
        Assert.Equal(90f, yaw, 3);
        Assert.Equal(45f, pitch, 3);
    }

    [Fact]
    public void TurnsAtMostMaxDeltaTheShortWayRound()
    {
        var (yaw, pitch) = Steering.TurnToward(170f, 0f, -170f, 30f, 10f);
        Assert.Equal(180f, MathF.Abs(yaw), 3);
        Assert.Equal(10f, pitch, 3);
    }

    [Fact]
    public void MovesForwardWhenFacingTheDirection()
    {
        var (forward, left) = Steering.ToLocal(new Vec3(1, 0, 0), 0f);
        Assert.Equal(1f, forward, 3);
        Assert.Equal(0f, left, 3);
    }

    [Fact]
    public void StrafesLeftWhenTheDirectionIsToTheLeft()
    {
        // Looking along +X, +Y is to the left.
        var (forward, left) = Steering.ToLocal(new Vec3(0, 5, 0), 0f);
        Assert.Equal(0f, forward, 3);
        Assert.Equal(1f, left, 3);
    }

    [Fact]
    public void BacksUpWhenTheDirectionIsBehind()
    {
        var (forward, left) = Steering.ToLocal(new Vec3(-1, 0, 0), 0f);
        Assert.Equal(-1f, forward, 3);
        Assert.Equal(0f, left, 3);
    }

    [Fact]
    public void StandsStillForAZeroDirection() => Assert.Equal((0f, 0f), Steering.ToLocal(Vec3.Zero, 37f));
}
