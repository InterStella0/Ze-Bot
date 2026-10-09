namespace ZeBots.Logic;

/// <summary>One moment of a player's movement.</summary>
/// <param name="Time">Seconds since the recording started.</param>
public readonly record struct Sample(float Time, Vec3 Position, Vec3 Velocity, bool OnGround, bool Ducked);

/// <summary>
/// How a real player got along one route edge that took more than walking (a jump, a crouch-jump,
/// a crouch): where they were and how fast they went, when they left the ground and when they
/// crouched, from the node before it to where they landed. A bot that keeps failing to make the
/// edge its own way replays this (<see cref="MimicRun"/>).
/// </summary>
public sealed class Recording
{
    /// <summary>Upward speed a jump starts with (sv_jump_impulse) and gravity (sv_gravity).</summary>
    public const float JumpImpulse = 301.993f;
    public const float Gravity = 800f;
    /// <summary>Leaving the ground faster than this upward was a jump, not a step off a ledge.</summary>
    private const float JumpRise = 100f;

    private readonly Sample[] _samples;

    /// <param name="samples">In time order; times are made relative to the first.</param>
    public Recording(IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0) throw new ArgumentException("A recording needs at least one sample.", nameof(samples));
        var start = samples[0].Time;
        _samples = samples.Select(sample => sample with { Time = sample.Time - start }).ToArray();
        TakeOff = FindTakeOff(_samples);
    }

    public IReadOnlyList<Sample> Samples => _samples;
    public Vec3 Start => _samples[0].Position;
    public Vec3 End => _samples[^1].Position;
    public float Duration => _samples[^1].Time;

    /// <summary>When the player jumped, or infinity if they never did.</summary>
    public float TakeOff { get; }

    /// <summary>Where the player was and how they moved at <paramref name="time"/>, between samples.</summary>
    public Sample At(float time)
    {
        if (time <= 0f) return _samples[0];
        if (time >= Duration) return _samples[^1];
        var next = 1;
        while (_samples[next].Time < time) next++;
        var a = _samples[next - 1];
        var b = _samples[next];
        var span = b.Time - a.Time;
        var t = span <= 0f ? 1f : (time - a.Time) / span;
        // Ground and crouch flip at the sample, not in between.
        return a with
        {
            Time = time,
            Position = a.Position + (b.Position - a.Position) * t,
            Velocity = a.Velocity + (b.Velocity - a.Velocity) * t,
        };
    }

    private static float FindTakeOff(Sample[] samples)
    {
        for (var i = 1; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (sample.OnGround || !samples[i - 1].OnGround || sample.Velocity.Z <= JumpRise) continue;
            // Samples are a few ticks apart: work back from how much the jump has slowed already.
            var since = (JumpImpulse - sample.Velocity.Z) / Gravity;
            return Math.Clamp(sample.Time - since, samples[i - 1].Time, sample.Time);
        }
        return float.PositiveInfinity;
    }
}

/// <summary>What a bot replaying a <see cref="Recording"/> does this tick.</summary>
/// <param name="Velocity">Horizontal velocity to give the bot.</param>
/// <param name="Jump">Jump now.</param>
/// <param name="Done">The replay is over.</param>
public readonly record struct MimicDrive(Vec3 Velocity, bool Jump, bool Duck, bool Done);

/// <summary>
/// A bot replaying how a player took an edge: from the recording's start it moves as the player
/// moved, pulled back onto their path when it drifts, and jumps and crouches when they did.
/// </summary>
public sealed class MimicRun(Recording recording, float start)
{
    /// <summary>How hard the bot is pulled back onto the recorded path (per second of error), and at most how fast.</summary>
    public const float Pull = 10f;
    public const float MaxPull = 120f;
    /// <summary>The replay ends this long after the recording does: time to land and settle.</summary>
    public const float Grace = 0.4f;
    /// <summary>Speed the usercmd drive takes as a full key press.</summary>
    private const float RunSpeed = 250f;

    public Recording Recording => recording;
    public float Start => start;
    /// <summary>The bot has taken its jump.</summary>
    public bool Jumped { get; private set; }

    public bool Over(float now) => now - start > recording.Duration + Grace;

    /// <summary>This tick's move. Taking the jump it says is due marks it taken.</summary>
    public MimicDrive Drive(float now, Vec3 position, bool onGround)
    {
        var drive = Peek(now, position, onGround);
        if (drive.Jump) Jumped = true;
        return drive;
    }

    /// <summary>Like <see cref="Drive"/>, without marking the jump taken.</summary>
    public MimicDrive Peek(float now, Vec3 position, bool onGround)
    {
        var time = now - start;
        if (time > recording.Duration + Grace) return new MimicDrive(Vec3.Zero, false, false, true);
        var sample = recording.At(time);
        var pull = new Vec3(sample.Position.X - position.X, sample.Position.Y - position.Y, 0) * Pull;
        if (pull.Length2D > MaxPull) pull = pull.Normalized2D() * MaxPull;
        // Past the end: only settle onto where the player landed.
        var along = time >= recording.Duration ? Vec3.Zero : new Vec3(sample.Velocity.X, sample.Velocity.Y, 0);
        var jump = !Jumped && onGround && time >= recording.TakeOff;
        return new MimicDrive(along + pull, jump, sample.Ducked, false);
    }

    /// <summary>The move as a fraction of run speed, for drives that press keys rather than set velocity.</summary>
    public static Vec3 AsMove(MimicDrive drive)
    {
        var speed = drive.Velocity.Length2D;
        return speed < 1f ? Vec3.Zero : drive.Velocity.Normalized2D() * MathF.Min(1f, speed / RunSpeed);
    }
}
