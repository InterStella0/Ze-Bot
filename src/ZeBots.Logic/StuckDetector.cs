namespace ZeBots.Logic;

/// <summary>What a stuck bot should add to its movement this pass.</summary>
/// <param name="StrafeLeft">Sideways move (-1..1) to slide off whatever the bot is pressed against.</param>
/// <param name="Teleport">Give up walking and teleport ahead along the trail.</param>
public readonly record struct StuckAction(bool Jump, bool Duck, float StrafeLeft, bool Teleport)
{
    public static readonly StuckAction None = new(false, false, 0f, false);
}

/// <summary>
/// Escalates when a bot that wants to move makes no progress: jump, then crouch-jump, then strafe,
/// then (after the catch-up time) teleport.
/// </summary>
public sealed class StuckDetector
{
    /// <summary>Moving this far from the last anchor counts as progress.</summary>
    public const float ProgressDistance = 32f;
    public const float JumpAfter = 0.5f;
    public const float StrafeAfter = 2f;
    private const float JumpInterval = 0.6f;
    private const float StrafeInterval = 0.7f;

    private Vec3 _anchor;
    private float _anchorTime;
    private bool _hasAnchor;
    private float _lastJump = float.NegativeInfinity;
    private int _jumps;
    private float _strafe;
    private float _strafeChosenAt = float.NegativeInfinity;

    /// <summary>Seconds since the bot last made progress (0 when it is not trying to move).</summary>
    public float StuckFor { get; private set; }

    public void Reset()
    {
        _hasAnchor = false;
        StuckFor = 0;
        _jumps = 0;
        _strafe = 0;
        _lastJump = float.NegativeInfinity;
        _strafeChosenAt = float.NegativeInfinity;
    }

    public StuckAction Update(Vec3 position, bool wantsToMove, float now, float catchupSeconds, Random random)
    {
        if (!wantsToMove || !_hasAnchor || Vec3.Distance(position, _anchor) >= ProgressDistance)
        {
            Reset();
            if (!wantsToMove) return StuckAction.None;
            _anchor = position;
            _anchorTime = now;
            _hasAnchor = true;
            return StuckAction.None;
        }

        StuckFor = now - _anchorTime;
        if (catchupSeconds > 0 && StuckFor >= catchupSeconds) return new StuckAction(false, false, 0f, true);
        if (StuckFor < JumpAfter) return StuckAction.None;

        var jump = false;
        var duck = false;
        if (now - _lastJump >= JumpInterval)
        {
            _lastJump = now;
            jump = true;
            // Every other jump is a crouch-jump, which clears higher ledges.
            duck = _jumps++ % 2 == 1;
        }

        var strafe = 0f;
        if (StuckFor >= StrafeAfter)
        {
            if (now - _strafeChosenAt >= StrafeInterval)
            {
                _strafeChosenAt = now;
                _strafe = random.Next(2) == 0 ? -1f : 1f;
            }
            strafe = _strafe;
        }
        return new StuckAction(jump, duck, strafe, false);
    }
}
