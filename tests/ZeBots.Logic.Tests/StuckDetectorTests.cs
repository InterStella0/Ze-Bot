using ZeBots.Logic;

namespace ZeBots.Logic.Tests;

public class StuckDetectorTests
{
    private static readonly Vec3 Wall = new(0, 0, 0);

    [Fact]
    public void DoesNothingWhileMakingProgress()
    {
        var stuck = new StuckDetector();
        var random = new Random(1);
        for (var i = 0; i < 100; i++)
        {
            var action = stuck.Update(new Vec3(i * 8, 0, 0), wantsToMove: true, now: i * 0.03f, catchupSeconds: 15, random);
            Assert.Equal(StuckAction.None, action);
        }
    }

    [Fact]
    public void DoesNothingWhenStandingOnPurpose()
    {
        var stuck = new StuckDetector();
        var random = new Random(1);
        for (var t = 0f; t < 30f; t += 0.5f) Assert.Equal(StuckAction.None, stuck.Update(Wall, wantsToMove: false, t, 15, random));
    }

    [Fact]
    public void EscalatesFromJumpToStrafeToTeleport()
    {
        var stuck = new StuckDetector();
        var random = new Random(1);
        stuck.Update(Wall, true, 0f, 15, random);

        Assert.Equal(StuckAction.None, stuck.Update(Wall, true, 0.3f, 15, random));

        var jump = stuck.Update(Wall, true, 0.6f, 15, random);
        Assert.True(jump.Jump);
        Assert.False(jump.Duck);
        Assert.Equal(0f, jump.StrafeLeft);

        var crouchJump = stuck.Update(Wall, true, 1.3f, 15, random);
        Assert.True(crouchJump.Jump);
        Assert.True(crouchJump.Duck);

        var strafe = stuck.Update(Wall, true, 2.5f, 15, random);
        Assert.NotEqual(0f, strafe.StrafeLeft);
        Assert.False(strafe.Teleport);

        Assert.True(stuck.Update(Wall, true, 15f, 15, random).Teleport);
    }

    [Fact]
    public void NeverTeleportsWhenCatchupIsOff()
    {
        var stuck = new StuckDetector();
        var random = new Random(1);
        stuck.Update(Wall, true, 0f, 0, random);
        Assert.False(stuck.Update(Wall, true, 600f, 0, random).Teleport);
    }

    [Fact]
    public void ProgressResetsTheTimer()
    {
        var stuck = new StuckDetector();
        var random = new Random(1);
        stuck.Update(Wall, true, 0f, 15, random);
        stuck.Update(Wall, true, 10f, 15, random);
        stuck.Update(new Vec3(100, 0, 0), true, 10.1f, 15, random);

        Assert.False(stuck.Update(new Vec3(100, 0, 0), true, 20f, 15, random).Teleport);
        Assert.True(stuck.Update(new Vec3(100, 0, 0), true, 25.2f, 15, random).Teleport);
    }
}
