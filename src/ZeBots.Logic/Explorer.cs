namespace ZeBots.Logic;

/// <summary>
/// Maps floor no one has walked yet, so bots can take routes the players have not shown them. From
/// each known node it probes the eight neighbouring points of a <see cref="Spacing"/> grid with
/// the world's traces (<see cref="IWorld.Probe"/>), adds the floor it finds as nodes, and links
/// them with how to get there: walk, drop, crouch, jump, crouch-jump or ladder. New nodes are
/// probed in turn, so the graph floods out over everything reachable. Its edges are marked
/// <see cref="Edge.Explored"/>: bots take them, but prefer the ways players have gone.
///
/// Work goes nearest-first from where players stand: every node a footstep adds, and every node a
/// player is on, starts a new wave. The plugin calls <see cref="Step"/> within a time budget each
/// tick, so a big map is mapped over a minute or two without stalling the server.
/// </summary>
public sealed class Explorer(WalkGraph graph)
{
    /// <summary>Grid spacing of explored nodes.</summary>
    public const float Spacing = 48f;
    /// <summary>A step up or down this small is walkable both ways.</summary>
    public const float StepHeight = 18f;

    private static readonly (int X, int Y)[] Directions = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    private readonly PriorityQueue<int, int> _frontier = new();
    /// <summary>Per node: probed outward already.</summary>
    private readonly List<bool> _explored = new();
    /// <summary>Per node: the lowest priority it is queued at (int.MaxValue when not queued).</summary>
    private readonly List<int> _queued = new();

    /// <summary>Nodes probed outward so far.</summary>
    public int Explored { get; private set; }

    /// <summary>Nodes waiting to be probed.</summary>
    public int Pending => _frontier.Count;

    public void Clear()
    {
        _frontier.Clear();
        _explored.Clear();
        _queued.Clear();
        Explored = 0;
    }

    /// <summary>A player stands on <paramref name="node"/>: map around it next.</summary>
    public void Seed(int node)
    {
        Sync();
        if (node >= 0 && node < _explored.Count) Enqueue(node, 0);
    }

    /// <summary>Probes outward from the next node in line. False when there is nothing left to map.</summary>
    public bool Step(IWorld world)
    {
        Sync();
        while (_frontier.TryDequeue(out var node, out var depth))
        {
            if (_explored[node]) continue;
            _explored[node] = true;
            _queued[node] = int.MaxValue;
            Explored++;
            Explore(node, depth, world);
            return true;
        }
        return false;
    }

    /// <summary>Nodes footsteps added since the last call are mapped around first.</summary>
    private void Sync()
    {
        while (_explored.Count < graph.Count)
        {
            var node = _explored.Count;
            _explored.Add(false);
            _queued.Add(int.MaxValue);
            Enqueue(node, 0);
        }
    }

    private void Enqueue(int node, int depth)
    {
        if (_explored[node] || _queued[node] <= depth) return;
        _queued[node] = depth;
        _frontier.Enqueue(node, depth);
    }

    private void Explore(int node, int depth, IWorld world)
    {
        var from = graph.Position(node);
        var baseX = MathF.Round(from.X / Spacing) * Spacing;
        var baseY = MathF.Round(from.Y / Spacing) * Spacing;
        // A footstep node off the grid first steps onto the grid point it is nearest.
        if (MathF.Abs(from.X - baseX) > 1f || MathF.Abs(from.Y - baseY) > 1f) TryStep(node, from, baseX, baseY, depth, world);
        foreach (var (dx, dy) in Directions) TryStep(node, from, baseX + dx * Spacing, baseY + dy * Spacing, depth, world);
    }

    private void TryStep(int node, Vec3 from, float x, float y, int depth, IWorld world)
    {
        if (LinkedTo(node, x, y)) return;
        var step = world.Probe(from, new Vec3(x, y, from.Z));
        if (step.Kind == StepKind.Blocked) return;
        var to = graph.Find(step.Floor, WalkGraph.MergeRadius, WalkGraph.MergeHeight);
        if (to == node) return;
        if (to < 0)
        {
            if (graph.Count >= WalkGraph.MaxNodes) return;
            to = graph.Add(step.Floor);
            _explored.Add(false);
            _queued.Add(int.MaxValue);
            Enqueue(to, depth + 1);
        }

        switch (step.Kind)
        {
            case StepKind.Walk:
                graph.AddEdge(node, new Edge(to, EdgeKind.Walk, default, Explored: true));
                graph.AddEdge(to, new Edge(node, EdgeKind.Walk, default, Explored: true));
                break;
            case StepKind.Drop:
                // The way back up, if there is one, is found when the lower node is mapped.
                graph.AddEdge(node, new Edge(to, EdgeKind.Walk, default, Explored: true));
                break;
            case StepKind.Crouch:
                graph.AddEdge(node, new Edge(to, EdgeKind.Walk, default, Moves.Crouch, Explored: true));
                if (MathF.Abs(step.Floor.Z - from.Z) <= StepHeight) graph.AddEdge(to, new Edge(node, EdgeKind.Walk, default, Moves.Crouch, Explored: true));
                break;
            case StepKind.Jump:
                graph.AddEdge(node, new Edge(to, EdgeKind.Walk, from, Moves.Jump, Explored: true));
                break;
            case StepKind.CrouchJump:
                graph.AddEdge(node, new Edge(to, EdgeKind.Walk, from, Moves.Jump | Moves.Crouch, Explored: true));
                break;
            case StepKind.Ladder:
                graph.AddEdge(node, new Edge(to, EdgeKind.Ladder, step.Via, Explored: true));
                // Down: get on at the top of the same ladder.
                graph.AddEdge(to, new Edge(node, EdgeKind.Ladder, new Vec3(step.Via.X, step.Via.Y, step.Floor.Z), Explored: true));
                break;
        }
    }

    /// <summary>Whether <paramref name="node"/> already has an edge to a node at that grid point.</summary>
    private bool LinkedTo(int node, float x, float y)
    {
        foreach (var edge in graph.Edges(node))
        {
            var position = graph.Position(edge.To);
            if (MathF.Abs(position.X - x) <= 1f && MathF.Abs(position.Y - y) <= 1f) return true;
        }
        return false;
    }
}
