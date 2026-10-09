namespace ZeBots.Logic;

/// <summary>
/// What makes one bot move differently from the next, rolled once per bot.
/// </summary>
/// <param name="Seed">Fixes the bot's route preferences and target preferences.</param>
/// <param name="Speed">Fraction of the weapon's run speed the bot moves at.</param>
/// <param name="Wander">Largest drift (degrees) off the straight line to the next waypoint.</param>
/// <param name="RouteNoise">Up to this much extra cost on each graph edge, by the seed, so bots
/// prefer different routes when there are several.</param>
/// <param name="TargetBias">Up to this much (either way) on how near each target seems, by the
/// seed, so zombies do not all pick the same human.</param>
/// <param name="FollowScale">Multiplies a human bot's follow distances: some stay close, some hang back.</param>
/// <param name="RetreatScale">Multiplies a human bot's retreat range: some back off earlier than others.</param>
/// <param name="Separation">How strongly the bot keeps its distance from teammates.</param>
/// <param name="Replan">Seconds between route searches.</param>
public sealed record Persona(
    int Seed,
    float Speed,
    float Wander,
    float RouteNoise,
    float TargetBias,
    float FollowScale,
    float RetreatScale,
    float Separation,
    float Replan)
{
    /// <summary>No variation at all; what tests use.</summary>
    public static readonly Persona Plain = new(0, 1f, 0f, 0f, 0f, 1f, 1f, 1f, 0.5f);

    public static Persona Roll(Random random)
    {
        float Between(float min, float max) => min + random.NextSingle() * (max - min);
        return new Persona(
            random.Next(),
            Speed: Between(0.92f, 1f),
            Wander: Between(4f, 22f),
            RouteNoise: Between(0.1f, 0.8f),
            TargetBias: Between(0.05f, 0.3f),
            FollowScale: Between(0.6f, 1.5f),
            RetreatScale: Between(0.7f, 1.6f),
            Separation: Between(0.6f, 1.4f),
            Replan: Between(0.4f, 0.8f));
    }

    /// <summary>How much more (or less) costly <paramref name="slot"/> looks to this bot as a target.</summary>
    public float TargetFactor(int slot) => 1f + TargetBias * (2f * Hash01(Seed, slot, 0x5bd1e995) - 1f);

    /// <summary>This bot's cost multiplier for the edge between two nodes (the same either way).</summary>
    public float EdgeFactor(int from, int to) =>
        RouteNoise <= 0f ? 1f : 1f + RouteNoise * Hash01(Seed, Math.Min(from, to), Math.Max(from, to));

    /// <summary>A stable pseudo-random number in [0, 1) for the given values.</summary>
    private static float Hash01(int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b + 0x7F4A7C15u + (h << 6) + (h >> 2);
            h ^= (uint)c + 0x94D049BBu + (h << 6) + (h >> 2);
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
