using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using ZeBots.Logic;

namespace ZeBots;

/// <summary>
/// <see cref="IWorld"/> through CounterStrikeSharp's traces: a player-sized box swept above step
/// height for walls, short rays down for floor along the way, a ray for what a shot would reach,
/// and for exploring, the moves a player
/// can make (step, drop, crouch under, jump or crouch-jump up, climb a ladder). Floor inside an
/// enabled trigger_hurt or trigger_teleport is not explored, so routes do not lead bots into them.
/// </summary>
internal sealed class TraceWorld : IWorld
{
    /// <summary>The box starts this high so stairs and small ledges do not count as walls.</summary>
    private const float StepClear = 20f;
    /// <summary>Floor must be within this far below the line (stairs and slopes going down).</summary>
    private const float FloorDepth = 72f;
    private const float FloorStart = 16f;
    private const float FloorSpacing = 128f;
    private const int MaxFloorProbes = 10;
    /// <summary>Footing ahead is checked this often along the way (a 45 degree slope drops less than a step).</summary>
    private const float FootingSpacing = 8f;
    /// <summary>
    /// A failed self-check this many times in a row, before any has passed, means the traces do not
    /// work. Once one has found floor they do: a player on another player, a prop or an edge misses
    /// it legitimately.
    /// </summary>
    private const int FailedChecksAllowed = 32;

    /// <summary>Exploring: highest drop taken (about where falling starts to hurt).</summary>
    private const float MaxDrop = 200f;
    /// <summary>Exploring: a jump sweeps over the ledge this high, crouched (a crouch-jump's reach).</summary>
    private const float JumpLift = 66f;
    private const float MaxJump = 50f;
    private const float MaxCrouchJump = 62f;
    /// <summary>Floor steeper than this (surface normal Z below it) cannot be stood on.</summary>
    private const float MinFloorNormal = 0.7f;
    private const int RampSteps = 4;
    private const float MaxRampRise = 48f;
    /// <summary>Ladders are looked for at this height, and climbed in steps of this much up to the limit.</summary>
    private const float LadderProbeHeight = 32f;
    private const float LadderStep = 32f;
    private const float MaxLadder = 1024f;
    /// <summary>trigger_hurt doing less than this per hit is not worth routing around.</summary>
    private const float HazardDamage = 10f;
    private const float PlayerHeight = 72f;

    // Players are excluded: a teammate in the way is not a wall.
    private static readonly TraceOptions Solid = new()
    {
        InteractsWith = Contents.Solid | Contents.PlayerClip | Contents.Window | Contents.PhysicsProp,
        InteractsExclude = Contents.Player | Contents.Npc,
    };

    // What stops a bullet: the world and props. Players are left out, so a teammate in the way
    // does not hide a zombie (shots go past them as often as not), and so is glass, which breaks.
    private static readonly TraceOptions Sight = new()
    {
        InteractsWith = Contents.Solid | Contents.PhysicsProp,
        InteractsExclude = Contents.Player | Contents.Npc | Contents.Window,
    };

    private static readonly TraceOptions Ladders = new()
    {
        InteractsWith = Contents.Ladder,
        InteractsExclude = Contents.Player | Contents.Npc,
    };

    // CounterStrikeSharp vectors live in native memory; reuse a few instead of allocating per trace.
    private readonly Vector _start = new(0, 0, 0);
    private readonly Vector _end = new(0, 0, 0);
    private readonly Vector _mins = new(-16, -16, 0);
    private readonly Vector _maxs = new(16, 16, 44);
    private readonly Vector _stand = new(16, 16, PlayerHeight);
    private readonly Vector _crouch = new(16, 16, 54);
    private readonly List<(Vec3 Min, Vec3 Max)> _hazards = new();
    private int _failedChecks;
    private bool _confirmed;

    /// <summary>Why traces were given up on; null while they work.</summary>
    public string? Failure { get; private set; }

    public bool Broken => Failure is not null;

    public int Hazards => _hazards.Count;

    public bool CanWalk(Vec3 from, Vec3 to)
    {
        if (Broken) return false;
        try
        {
            Set(_start, from, StepClear);
            Set(_end, to, StepClear);
            if (Trace.TraceHullShape(_start, _end, _mins, _maxs, null, Solid).DidHit()) return false;

            var probes = Math.Clamp((int)(Vec3.Distance2D(from, to) / FloorSpacing), 1, MaxFloorProbes);
            for (var i = 1; i <= probes; i++)
            {
                var t = i / (probes + 1f);
                if (!HasFloor(from + (to - from) * t)) return false;
            }
            return true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            return false;
        }
    }

    public bool CanSee(Vec3 from, Vec3 to)
    {
        if (Broken) return false;
        try
        {
            Set(_start, from, 0f);
            Set(_end, to, 0f);
            return !Trace.TraceEndShape(_start, _end, null, Sight).DidHit();
        }
        catch (Exception error)
        {
            Failure = error.Message;
            return false;
        }
    }

    public Step Probe(Vec3 from, Vec3 to)
    {
        if (Broken) return Step.Blocked;
        try
        {
            if (InHazard(to)) return Step.Blocked;
            if (Move(from, to, _stand, Explorer.StepHeight) is { } floor) return Level(from, floor, StepKind.Walk);
            if (Ramp(from, to) is { } ramp) return new Step(StepKind.Walk, ramp);
            if (Move(from, to, _crouch, Explorer.StepHeight) is { } low) return Level(from, low, StepKind.Crouch);
            return Jump(from, to) ?? Ladder(from, to) ?? Step.Blocked;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            return Step.Blocked;
        }
    }

    public bool KeepsFooting(Vec3 from, Vec3 to)
    {
        if (Broken) return true;
        try
        {
            var samples = Math.Max(1, (int)MathF.Ceiling(Vec3.Distance2D(from, to) / FootingSpacing));
            var floor = from.Z;
            for (var i = 1; i <= samples; i++)
            {
                var at = from + (to - from) * (i / (float)samples);
                // The player's box, from a step above the floor so far down to a step below it:
                // stairs and slopes go on, a ledge has nothing there.
                Set(_start, at with { Z = floor }, Explorer.StepHeight);
                Set(_end, at with { Z = floor }, -Explorer.StepHeight);
                var result = Trace.TraceHullShape(_start, _end, _mins, _maxs, null, Solid);
                if (result.DidHit() && result.Fraction <= 0f) return true;
                if (!result.DidHit() || result.Normal.Z < MinFloorNormal) return false;
                floor = result.EndPos.Z;
                if (InHazard(new Vec3(at.X, at.Y, floor))) return false;
            }
            return true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            return true;
        }
    }

    /// <summary>
    /// Checks the traces against a player the game says is on the ground: there must be floor
    /// under them. Missed every time from the start and <see cref="Failure"/> is set.
    /// </summary>
    public void Check(Vec3 feet)
    {
        if (Broken || _confirmed) return;
        try
        {
            // The player's box, not a ray from their middle: someone on an edge stands on its rim.
            if (HasFloor(feet) || Down(feet, FloorStart, FloorStart + FloorDepth, _stand) is not null)
            {
                _confirmed = true;
                return;
            }
            if (++_failedChecks >= FailedChecksAllowed) Failure = "no floor found under players standing on the ground";
        }
        catch (Exception error)
        {
            Failure = error.Message;
        }
    }

    /// <summary>Re-reads which hurt and teleport triggers are on (maps switch them during a round).</summary>
    public void RefreshHazards()
    {
        _hazards.Clear();
        foreach (var hurt in Utilities.FindAllEntitiesByDesignerName<CTriggerHurt>("trigger_hurt"))
        {
            if (hurt.IsValid && !hurt.Disabled && hurt.Damage >= HazardDamage) AddHazard(hurt);
        }
        foreach (var teleport in Utilities.FindAllEntitiesByDesignerName<CTriggerTeleport>("trigger_teleport"))
        {
            if (teleport.IsValid && !teleport.Disabled) AddHazard(teleport);
        }
    }

    private void AddHazard(CBaseEntity entity)
    {
        var origin = entity.AbsOrigin;
        var collision = entity.Collision;
        if (origin is null || collision is null) return;
        var mins = collision.Mins;
        var maxs = collision.Maxs;
        _hazards.Add((new Vec3(origin.X + mins.X, origin.Y + mins.Y, origin.Z + mins.Z), new Vec3(origin.X + maxs.X, origin.Y + maxs.Y, origin.Z + maxs.Z)));
    }

    /// <summary>Whether a player standing at <paramref name="feet"/> would touch one of the hazards.</summary>
    private bool InHazard(Vec3 feet)
    {
        foreach (var (min, max) in _hazards)
        {
            if (feet.X + 16f < min.X || feet.X - 16f > max.X) continue;
            if (feet.Y + 16f < min.Y || feet.Y - 16f > max.Y) continue;
            if (feet.Z + PlayerHeight < min.Z || feet.Z > max.Z) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sweeps a player box from <paramref name="from"/> to <paramref name="to"/>, lifted by
    /// <paramref name="lift"/>, then down onto the floor there: where the player ends up, if anywhere.
    /// </summary>
    private Vec3? Move(Vec3 from, Vec3 to, Vector maxs, float lift)
    {
        Set(_start, from, lift);
        Set(_end, to, lift);
        if (Trace.TraceHullShape(_start, _end, _mins, maxs, null, Solid).DidHit()) return null;
        return Down(to, lift, lift + MaxDrop, maxs);
    }

    /// <summary>The floor a player box lands on, dropped from <paramref name="lift"/> above <paramref name="at"/>.</summary>
    private Vec3? Down(Vec3 at, float lift, float depth, Vector maxs)
    {
        Set(_start, at, lift);
        Set(_end, at, lift - depth);
        var result = Trace.TraceHullShape(_start, _end, _mins, maxs, null, Solid);
        // Nothing below, started inside something, or too steep to stand on.
        if (!result.DidHit() || result.Fraction <= 0f || result.Normal.Z < MinFloorNormal) return null;
        var floor = new Vec3(at.X, at.Y, result.EndPos.Z);
        return InHazard(floor) ? null : floor;
    }

    /// <summary>A slope too steep to step up in one go but fine to walk: in a few small steps.</summary>
    private Vec3? Ramp(Vec3 from, Vec3 to)
    {
        var at = from;
        for (var i = 1; i <= RampSteps; i++)
        {
            var next = from + (to - from) * (i / (float)RampSteps);
            if (Move(at, next with { Z = at.Z }, _stand, Explorer.StepHeight) is not { } floor) return null;
            at = floor;
        }
        return MathF.Abs(at.Z - from.Z) <= MaxRampRise ? at : null;
    }

    private static Step Level(Vec3 from, Vec3 floor, StepKind kind)
    {
        if (floor.Z >= from.Z - Explorer.StepHeight) return new Step(kind, floor);
        // A drop that also needs crouching would be a crouch on one side only; not worth the trouble.
        return kind == StepKind.Walk ? new Step(StepKind.Drop, floor) : Step.Blocked;
    }

    /// <summary>Up onto something a step is too high for: room to jump, clear over it, floor on top.</summary>
    private Step? Jump(Vec3 from, Vec3 to)
    {
        Set(_start, from, Explorer.StepHeight);
        Set(_end, from, JumpLift);
        if (Trace.TraceHullShape(_start, _end, _mins, _crouch, null, Solid).DidHit()) return null;
        Set(_start, from, JumpLift);
        Set(_end, to, JumpLift);
        if (Trace.TraceHullShape(_start, _end, _mins, _crouch, null, Solid).DidHit()) return null;
        // Only floor above step height counts: lower down it is a gap, not a ledge.
        if (Down(to, JumpLift, JumpLift - Explorer.StepHeight, _crouch) is not { } floor) return null;
        var rise = floor.Z - from.Z;
        if (rise <= MaxJump) return new Step(StepKind.Jump, floor);
        if (rise <= MaxCrouchJump) return new Step(StepKind.CrouchJump, floor);
        return null;
    }

    /// <summary>A ladder ahead: how high it goes, and the floor at its top.</summary>
    private Step? Ladder(Vec3 from, Vec3 to)
    {
        var direction = (to - from).Normalized2D();
        var reach = to + direction * 16f;
        Set(_start, from, LadderProbeHeight);
        Set(_end, reach, LadderProbeHeight);
        var hit = Trace.TraceEndShape(_start, _end, null, Ladders);
        if (!hit.DidHit() || hit.Fraction <= 0f) return null;
        var face = new Vec3(hit.EndPos.X, hit.EndPos.Y, from.Z);

        var top = LadderProbeHeight;
        for (var height = LadderProbeHeight + LadderStep; height <= MaxLadder; height += LadderStep)
        {
            Set(_start, from, height);
            Set(_end, reach, height);
            if (!Trace.TraceEndShape(_start, _end, null, Ladders).DidHit()) break;
            top = height;
        }

        // Step off over the top, onto whatever is just past the ladder.
        var landing = face + direction * 32f;
        if (Down(landing, top + PlayerHeight, PlayerHeight + LadderStep * 2f, _stand) is not { } floor) return null;
        if (floor.Z <= from.Z + MaxCrouchJump) return null;
        return new Step(StepKind.Ladder, floor, face - direction * 16f);
    }

    private bool HasFloor(Vec3 at)
    {
        Set(_start, at, FloorStart);
        Set(_end, at, -FloorDepth);
        return Trace.TraceEndShape(_start, _end, null, Solid).DidHit();
    }

    private static void Set(Vector vector, Vec3 position, float lift)
    {
        vector.X = position.X;
        vector.Y = position.Y;
        vector.Z = position.Z + lift;
    }
}
