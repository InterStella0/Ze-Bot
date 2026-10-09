using ZeBots.Logic;
using static ZeBots.Logic.Tests.Walks;

namespace ZeBots.Logic.Tests;

public class RecordingTests
{
    /// <summary>A run east from x = 0, a jump at x = 62 and a landing on a 48-unit ledge, sampled every 1/32 s.</summary>
    internal static Recording LedgeJump()
    {
        var samples = new List<Sample>();
        const float run = 250f;
        const float takeOff = 0.25f;
        for (var t = 0f; t <= 1f; t += 1f / 32f)
        {
            var x = run * t;
            if (t < takeOff)
            {
                samples.Add(new Sample(10f + t, V(x, 0), V(run, 0), true, false));
                continue;
            }
            var air = t - takeOff;
            var z = Recording.JumpImpulse * air - Recording.Gravity / 2f * air * air;
            var vz = Recording.JumpImpulse - Recording.Gravity * air;
            if (z < 48f && vz < 0f)
            {
                samples.Add(new Sample(10f + t, V(x, 0, 48), V(run, 0), true, false));
                continue;
            }
            samples.Add(new Sample(10f + t, V(x, 0, z), V(run, 0, vz), false, air > 0.1f));
        }
        return new Recording(samples);
    }

    [Fact]
    public void FindsWhenThePlayerJumped()
    {
        var recording = LedgeJump();

        Assert.Equal(0f, recording.Samples[0].Time);
        Assert.Equal(0.25f, recording.TakeOff, 2);
        Assert.Equal(V(0, 0), recording.Start);
        Assert.Equal(48f, recording.End.Z);
    }

    [Fact]
    public void InterpolatesBetweenSamples()
    {
        var sample = LedgeJump().At(0.1f);

        Assert.Equal(25f, sample.Position.X, 1);
        Assert.True(sample.OnGround);
    }

    [Fact]
    public void AWalkHasNoJump()
    {
        var recording = new Recording([new Sample(0f, V(0, 0), V(250, 0), true, true), new Sample(0.5f, V(125, 0), V(250, 0), true, true)]);
        Assert.True(float.IsPositiveInfinity(recording.TakeOff));
    }

    [Fact]
    public void ReplayMovesAsThePlayerDidAndJumpsOnce()
    {
        var run = new MimicRun(LedgeJump(), start: 100f);

        var first = run.Drive(100f, V(0, 0), onGround: true);
        Assert.Equal(250f, first.Velocity.X, 1);
        Assert.False(first.Jump);

        Assert.True(run.Drive(100.26f, V(65, 0), onGround: true).Jump);
        Assert.True(run.Jumped);
        Assert.False(run.Drive(100.27f, V(67, 0), onGround: true).Jump);
    }

    [Fact]
    public void ReplayPullsTheBotBackOntoThePath()
    {
        var run = new MimicRun(LedgeJump(), start: 0f);

        // 8 units south of where the player was: pushed north as well as east.
        var drive = run.Peek(0.1f, V(25, -8), onGround: true);

        Assert.Equal(250f, drive.Velocity.X, 1);
        Assert.Equal(8f * MimicRun.Pull, drive.Velocity.Y, 1);
        Assert.False(run.Jumped);
    }

    [Fact]
    public void ReplayEndsAfterTheRecording()
    {
        var recording = LedgeJump();
        var run = new MimicRun(recording, start: 0f);

        Assert.False(run.Drive(recording.Duration + MimicRun.Grace / 2f, recording.End, onGround: true).Done);
        Assert.True(run.Drive(recording.Duration + MimicRun.Grace * 2f, recording.End, onGround: true).Done);
        Assert.True(run.Over(recording.Duration + MimicRun.Grace * 2f));
    }

    [Fact]
    public void ARealPlayersJumpIsRecordedForItsEdge()
    {
        var graph = new WalkGraph();
        foreach (var sample in LedgeJump().Samples) graph.Record(1, sample.Position, sample.OnGround, null, sample.Ducked, now: sample.Time, velocity: sample.Velocity, real: true);

        var from = graph.Find(V(0, 0), 30f, 30f);
        var to = graph.Find(LedgeJump().End, 30f, 30f);
        var jump = Enumerable.Range(0, graph.Count)
            .SelectMany(node => graph.Edges(node).Select(edge => (From: node, Edge: edge)))
            .Single(entry => (entry.Edge.Moves & Moves.Jump) != 0);

        Assert.True(from >= 0 && to >= 0);
        var recording = graph.RecordingOf(jump.From, jump.Edge.To);
        Assert.NotNull(recording);
        Assert.True(recording.TakeOff < recording.Duration);
        Assert.Equal(48f, recording.End.Z);
    }

    [Fact]
    public void BotsAreNotRecorded()
    {
        var graph = new WalkGraph();
        foreach (var sample in LedgeJump().Samples) graph.Record(1, sample.Position, sample.OnGround, null, sample.Ducked, now: sample.Time, velocity: sample.Velocity, real: false);

        Assert.Equal(0, graph.Recordings);
    }
}
