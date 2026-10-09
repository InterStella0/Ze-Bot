namespace ZeBots.Logic;

/// <summary>How well a bot aims, from <c>bot_difficulty</c>.</summary>
/// <param name="TurnRate">Maximum view rotation in degrees per second.</param>
/// <param name="Jitter">Random aim offset in degrees, so bots do not hit every shot.</param>
/// <param name="FireTolerance">A human bot only shoots once its aim is this close (degrees).</param>
public readonly record struct AimSkill(float TurnRate, float Jitter, float FireTolerance)
{
    public static AimSkill ForDifficulty(int difficulty) => Math.Clamp(difficulty, 0, 3) switch
    {
        0 => new AimSkill(240f, 4f, 10f),
        1 => new AimSkill(420f, 2.5f, 7f),
        2 => new AimSkill(600f, 1.5f, 5f),
        _ => new AimSkill(900f, 0.6f, 3f),
    };
}

/// <summary>Tunables, mostly from the <c>zb_*</c> convars.</summary>
public sealed record BrainSettings
{
    /// <summary>A human bot that stopped following starts again past this walking distance (zb_follow_max), scaled by its persona.</summary>
    public float FollowMax { get; init; } = 400f;
    /// <summary>A following human bot stops inside this walking distance of its leader (zb_follow_min), scaled by its persona.</summary>
    public float FollowMin { get; init; } = 200f;
    /// <summary>
    /// A bot stuck this long is teleported along its route, and teleporters on the route are crossed
    /// by teleport (zb_catchup_seconds). 0, the default, never teleports: bots only walk.
    /// </summary>
    public float CatchupSeconds { get; init; } = 0f;
    /// <summary>A zombie with a clear line to its target runs straight at it inside this range.</summary>
    public float ChaseRange { get; init; } = 1500f;
    /// <summary>A zombie attacks inside this range; ZR infects on the knife hit.</summary>
    public float KnifeRange { get; init; } = 80f;
    /// <summary>A human bot shoots zombies it can see inside this range.</summary>
    public float EngageRange { get; init; } = 2000f;
    /// <summary>A human bot backs away from zombies inside this range.</summary>
    public float RetreatRange { get; init; } = 150f;
    /// <summary>Human bots go to a real player's ping and hold it this long (zb_ping_seconds).</summary>
    public float PingSeconds { get; init; } = 20f;
    public AimSkill Aim { get; init; } = AimSkill.ForDifficulty(1);
}
