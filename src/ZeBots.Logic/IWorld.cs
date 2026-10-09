namespace ZeBots.Logic;

/// <summary>What ZeBots can ask the map, through the engine's traces.</summary>
public interface IWorld
{
    /// <summary>
    /// Whether a player could walk in a straight line between two feet positions: nothing solid in
    /// the way and floor under it. Used both to see a target and to know a shortcut is safe.
    /// </summary>
    bool CanWalk(Vec3 from, Vec3 to);

    /// <summary>Whether nothing solid is between two points: a shot from one would reach the other.</summary>
    bool CanSee(Vec3 from, Vec3 to);

    /// <summary>
    /// How a player standing at <paramref name="from"/> gets to the floor at
    /// (<paramref name="to"/>.X, <paramref name="to"/>.Y), a short step away, if they can. Used by
    /// the <see cref="Explorer"/> to map floor no one has walked.
    /// </summary>
    Step Probe(Vec3 from, Vec3 to);

    /// <summary>
    /// Whether a player walking on the ground from <paramref name="from"/> toward
    /// <paramref name="to"/> keeps their footing: no ledge further down than a step and nothing
    /// that hurts on the way. A wall counts as footing: the player stops at it.
    /// </summary>
    bool KeepsFooting(Vec3 from, Vec3 to);
}

public enum StepKind
{
    Blocked,
    /// <summary>Level, or a stair step up or down: walkable both ways.</summary>
    Walk,
    /// <summary>Down further than a step: one way.</summary>
    Drop,
    /// <summary>Only fits crouched (a vent, a low ceiling).</summary>
    Crouch,
    /// <summary>Up onto a ledge too high to step onto.</summary>
    Jump,
    /// <summary>Up onto a ledge only a crouch-jump clears.</summary>
    CrouchJump,
    /// <summary>Up a ladder that starts at <see cref="Step.Via"/>.</summary>
    Ladder,
}

/// <param name="Floor">Feet position on the floor reached.</param>
/// <param name="Via">For a ladder, where to get on it.</param>
public readonly record struct Step(StepKind Kind, Vec3 Floor, Vec3 Via = default)
{
    public static readonly Step Blocked = new(StepKind.Blocked, default);
}
