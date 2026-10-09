using ZeBots.Logic;

namespace ZeBots.Logic.Tests;

/// <summary>
/// A map for <see cref="IWorld"/>: floor at height 0 (inside optional bounds), walls (2D segments),
/// raised platforms and low ceilings.
/// </summary>
internal sealed class TestWorld : IWorld
{
    private readonly List<(Vec3 A, Vec3 B)> _walls = new();
    private readonly List<(float MinX, float MinY, float MaxX, float MaxY, float Height)> _platforms = new();
    private readonly List<(float MinX, float MinY, float MaxX, float MaxY)> _lowCeilings = new();
    private (float MinX, float MinY, float MaxX, float MaxY)? _bounds;

    public static TestWorld Open() => new();

    /// <summary>No floor outside this rectangle (so exploring it ends).</summary>
    public static TestWorld Bounded(float minX, float minY, float maxX, float maxY) => new() { _bounds = (minX, minY, maxX, maxY) };

    public TestWorld Wall(float ax, float ay, float bx, float by)
    {
        _walls.Add((new Vec3(ax, ay, 0), new Vec3(bx, by, 0)));
        return this;
    }

    /// <summary>The four sides of an axis-aligned box.</summary>
    public TestWorld Box(float minX, float minY, float maxX, float maxY) =>
        Wall(minX, minY, maxX, minY).Wall(maxX, minY, maxX, maxY).Wall(maxX, maxY, minX, maxY).Wall(minX, maxY, minX, minY);

    /// <summary>Raised floor: the top of a crate or a ledge.</summary>
    public TestWorld Platform(float minX, float minY, float maxX, float maxY, float height)
    {
        _platforms.Add((minX, minY, maxX, maxY, height));
        return this;
    }

    /// <summary>Somewhere only a crouching player fits.</summary>
    public TestWorld LowCeiling(float minX, float minY, float maxX, float maxY)
    {
        _lowCeilings.Add((minX, minY, maxX, maxY));
        return this;
    }

    public int Checks { get; private set; }
    public int Probes { get; private set; }
    public int Sights { get; private set; }

    public bool CanWalk(Vec3 from, Vec3 to)
    {
        Checks++;
        if (_walls.Any(wall => Crosses(from, to, wall.A, wall.B))) return false;
        // Level all the way, with room to stand: no ledge up or down, no low ceiling.
        var samples = Math.Max(1, (int)(Vec3.Distance2D(from, to) / 16f));
        var floor = FloorAt(from);
        for (var i = 1; i <= samples; i++)
        {
            var at = from + (to - from) * (i / (float)samples);
            var next = FloorAt(at);
            if (next is null || floor is null || MathF.Abs(next.Value - floor.Value) > Explorer.StepHeight) return false;
            if (_lowCeilings.Any(area => Inside(at, area.MinX, area.MinY, area.MaxX, area.MaxY))) return false;
            floor = next;
        }
        return true;
    }

    /// <summary>Walls block sight; floors, ledges and ceilings do not.</summary>
    public bool CanSee(Vec3 from, Vec3 to)
    {
        Sights++;
        return !_walls.Any(wall => Crosses(from, to, wall.A, wall.B));
    }

    public Step Probe(Vec3 from, Vec3 to)
    {
        Probes++;
        if (_walls.Any(wall => Crosses(from, to, wall.A, wall.B))) return Step.Blocked;
        if (FloorAt(to) is not { } height) return Step.Blocked;
        var floor = new Vec3(to.X, to.Y, height);
        var rise = height - from.Z;
        // The player passes under the ceiling at either end.
        var low = _lowCeilings.Any(area => Inside(to, area.MinX, area.MinY, area.MaxX, area.MaxY) || Inside(from, area.MinX, area.MinY, area.MaxX, area.MaxY));
        if (MathF.Abs(rise) <= Explorer.StepHeight) return new Step(low ? StepKind.Crouch : StepKind.Walk, floor);
        if (low) return Step.Blocked;
        if (rise < 0f) return rise >= -200f ? new Step(StepKind.Drop, floor) : Step.Blocked;
        if (rise <= 48f) return new Step(StepKind.Jump, floor);
        if (rise <= 62f) return new Step(StepKind.CrouchJump, floor);
        return Step.Blocked;
    }

    public int Footings { get; private set; }

    public bool KeepsFooting(Vec3 from, Vec3 to)
    {
        Footings++;
        var samples = Math.Max(1, (int)MathF.Ceiling(Vec3.Distance2D(from, to) / 8f));
        var floor = FloorAt(from) ?? from.Z;
        for (var i = 1; i <= samples; i++)
        {
            var at = from + (to - from) * (i / (float)samples);
            if (_walls.Any(wall => Crosses(from, at, wall.A, wall.B))) return true;
            if (FloorAt(at) is not { } next) return false;
            if (next - floor > Explorer.StepHeight) return true;
            if (floor - next > Explorer.StepHeight) return false;
            floor = next;
        }
        return true;
    }

    private float? FloorAt(Vec3 at)
    {
        if (_bounds is { } bounds && !Inside(at, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY)) return null;
        var height = 0f;
        foreach (var platform in _platforms)
        {
            if (Inside(at, platform.MinX, platform.MinY, platform.MaxX, platform.MaxY)) height = MathF.Max(height, platform.Height);
        }
        return height;
    }

    private static bool Inside(Vec3 at, float minX, float minY, float maxX, float maxY) =>
        at.X >= minX && at.X <= maxX && at.Y >= minY && at.Y <= maxY;

    private static bool Crosses(Vec3 p, Vec3 q, Vec3 a, Vec3 b)
    {
        static float Cross(Vec3 o, Vec3 u, Vec3 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        var d1 = Cross(a, b, p);
        var d2 = Cross(a, b, q);
        var d3 = Cross(p, q, a);
        var d4 = Cross(p, q, b);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }
}

internal static class Walks
{
    /// <summary>Records <paramref name="slot"/> walking along the points, a footstep every 8 units.</summary>
    public static void Walk(this WalkGraph graph, int slot, IWorld? world, params Vec3[] points) =>
        graph.WalkAs(slot, world, ducked: false, points);

    /// <summary>Like <see cref="Walk"/>, crouched or not.</summary>
    public static void WalkAs(this WalkGraph graph, int slot, IWorld? world, bool ducked, params Vec3[] points)
    {
        graph.Record(slot, points[0], onGround: true, world, ducked);
        for (var i = 1; i < points.Length; i++)
        {
            var from = points[i - 1];
            var to = points[i];
            var steps = Math.Max(1, (int)(Vec3.Distance(from, to) / 8f));
            for (var s = 1; s <= steps; s++) graph.Record(slot, from + (to - from) * (s / (float)steps), onGround: true, world, ducked);
        }
    }

    /// <summary>Records <paramref name="slot"/> climbing a ladder straight up from <paramref name="foot"/> to <paramref name="top"/>.</summary>
    public static void ClimbLadder(this WalkGraph graph, int slot, Vec3 foot, Vec3 top)
    {
        for (var z = foot.Z + 8f; z < top.Z; z += 8f) graph.Record(slot, new Vec3(foot.X, foot.Y, z), onGround: false, null, onLadder: true);
        graph.Record(slot, top, onGround: true, null);
    }

    /// <summary>Records <paramref name="slot"/> jumping from <paramref name="from"/> to land on <paramref name="to"/>.</summary>
    public static void Jump(this WalkGraph graph, int slot, Vec3 from, Vec3 to, bool tucked = false)
    {
        var peak = MathF.Max(from.Z, to.Z) + 20f;
        for (var t = 0.2f; t < 1f; t += 0.2f)
        {
            var at = from + (to - from) * t;
            graph.Record(slot, at with { Z = peak }, onGround: false, null, ducked: tucked);
        }
        graph.Record(slot, to, onGround: true, null);
    }

    /// <summary>Records <paramref name="slot"/> being moved to <paramref name="to"/> at once, as a teleporter does.</summary>
    public static void Teleported(this WalkGraph graph, int slot, IWorld? world, Vec3 to) => graph.Record(slot, to, onGround: true, world);

    public static Vec3 V(float x, float y, float z = 0) => new(x, y, z);

    /// <summary>Maps everything reachable from the nodes the graph has.</summary>
    public static int ExploreAll(this WalkGraph graph, IWorld world)
    {
        var explorer = new Explorer(graph);
        var steps = 0;
        while (explorer.Step(world)) steps++;
        return steps;
    }
}
