namespace ZeBots.Logic;

/// <summary>
/// The routes zombies are taking, so the next zombie to plan can prefer another one: a horde
/// spreads over every way to the humans instead of queueing down one corridor. Routes are claimed
/// by area rather than by node, because one corridor holds many parallel chains of nodes.
/// </summary>
public sealed class Crowd
{
    /// <summary>Only this much of a route ahead counts; past it, routes merge anyway.</summary>
    public const int ClaimNodes = 40;
    /// <summary>Extra cost, per zombie already routed through an area, of entering a node in it.</summary>
    public const float NodePenalty = 24f;
    private const float CellSize = 128f;

    private readonly Dictionary<int, HashSet<(int, int, int)>> _claims = new();
    private readonly Dictionary<(int, int, int), int> _counts = new();

    public void Claim(int slot, IEnumerable<Vec3> route)
    {
        Release(slot);
        var cells = route.Take(ClaimNodes).Select(Cell).ToHashSet();
        _claims[slot] = cells;
        foreach (var cell in cells) _counts[cell] = _counts.GetValueOrDefault(cell) + 1;
    }

    public void Release(int slot)
    {
        if (!_claims.Remove(slot, out var cells)) return;
        foreach (var cell in cells)
        {
            if (--_counts[cell] <= 0) _counts.Remove(cell);
        }
    }

    public void Clear()
    {
        _claims.Clear();
        _counts.Clear();
    }

    public float Penalty(Vec3 position) => _counts.TryGetValue(Cell(position), out var count) ? count * NodePenalty : 0f;

    private static (int, int, int) Cell(Vec3 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Y / CellSize), (int)MathF.Floor(position.Z / CellSize));
}
