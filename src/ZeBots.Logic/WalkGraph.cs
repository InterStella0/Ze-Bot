namespace ZeBots.Logic;

public enum EdgeKind
{
    /// <summary>Walk (or drop) from one node to the other, with whatever <see cref="Moves"/> it takes.</summary>
    Walk,
    /// <summary>Someone was moved further than anyone can walk (a teleporter): walk into <see cref="Edge.Via"/> to take it.</summary>
    Teleport,
    /// <summary>Climb (or climb down) a ladder that starts at <see cref="Edge.Via"/>.</summary>
    Ladder,
}

/// <summary>What a walk edge takes besides walking.</summary>
[Flags]
public enum Moves : byte
{
    None = 0,
    /// <summary>Jump at <see cref="Edge.Via"/> (onto a ledge, across a gap).</summary>
    Jump = 1,
    /// <summary>Crouch: low ceiling, or with <see cref="Jump"/>, a crouch-jump.</summary>
    Crouch = 2,
}

/// <param name="Via">Teleport: where the player was heading when the map moved them. Ladder: where
/// to get on. Jump: where to take off.</param>
/// <param name="Explored">Only the <see cref="Explorer"/>'s traces say this way works; no player has
/// shown it. Such edges cost more, so bots keep to the ways players go.</param>
public readonly record struct Edge(int To, EdgeKind Kind, Vec3 Via, Moves Moves = Moves.None, bool Explored = false)
{
    /// <summary>Plain walking: no jump, crouch, ladder or teleport. Only these may be cut across.</summary>
    public bool Plain => Kind == EdgeKind.Walk && Moves == Moves.None;

    /// <summary>How hard the edge is to take; of two edges between the same nodes, the easier one is kept.</summary>
    internal int Difficulty => Kind switch
    {
        EdgeKind.Teleport => 10,
        EdgeKind.Ladder => 5,
        _ => (Moves & Moves.Jump) != 0 ? 2 + ((Moves & Moves.Crouch) != 0 ? 1 : 0) : (Moves & Moves.Crouch) != 0 ? 1 : 0,
    };
}

/// <summary>
/// A map of where players can go, standing in for the nav mesh ZE maps do not ship. It grows two
/// ways: every player's footsteps add nodes and edges (how they got there: a walk, a jump, a
/// crouch, a ladder, a teleporter), and the <see cref="Explorer"/> probes outward from known nodes
/// with traces to find floor no one has walked yet. With an <see cref="IWorld"/>, a new footstep
/// node is also linked to nearby nodes it has a clear, floored line to. The graph lives for the
/// whole map: later rounds start knowing the routes earlier ones found.
/// </summary>
public sealed class WalkGraph
{
    /// <summary>A footstep this close to a node is on that node.</summary>
    public const float MergeRadius = 28f;
    public const float MergeHeight = 36f;
    /// <summary>A walker adds a node once this far from its last one.</summary>
    public const float NodeSpacing = 48f;
    /// <summary>New nodes are linked to nodes this close (and this level) they can walk straight to.</summary>
    public const float LinkRadius = 112f;
    public const float LinkHeight = 24f;
    private const int MaxLinks = 6;
    /// <summary>The highest step back up a recorded drop that still counts as walkable (a crouch-jump).</summary>
    public const float MaxClimb = 56f;
    /// <summary>A footstep this much higher than the last one, without touching the ground, was a jump.</summary>
    private const float JumpRise = 12f;
    /// <summary>
    /// Higher than a crouch-jump reaches: whoever got up there was carried (an elevator, a push, a
    /// bot driven up a ladder), which is no step anyone can take on purpose.
    /// </summary>
    public const float MaxJumpRise = 66f;
    /// <summary>Moving further than this between two samples (~30 ms) is a teleport, not a step.</summary>
    public const float GapDistance = 512f;
    /// <summary>How far past the last footstep before a teleport a bot walks to find the trigger.</summary>
    public const float GapWalkOn = 96f;
    public const int MaxNodes = 250000;
    private const float CellSize = 64f;
    private const float TeleportCost = 32f;
    /// <summary>Extra cost of a jump, and how much slower crouching and ladders are than walking.</summary>
    private const float JumpCost = 24f;
    private const float CrouchFactor = 2.5f;
    private const float LadderFactor = 1.5f;
    /// <summary>How much more an edge only the traces found costs than one a player showed.</summary>
    public const float ExploredFactor = 1.5f;
    /// <summary>A walker's samples since its last node are kept up to this many (a long fall, a slow crouch).</summary>
    private const int MaxTrail = 256;

    private sealed class Walker
    {
        public int Node = -1;
        public Vec3? LastSample;
        public Vec3 LastGround;
        public Vec3 Heading;
        public bool GapPending;
        /// <summary>Since the last node: rose off the ground (jumped), crouched, rode a ladder.</summary>
        public bool Rose;
        public bool Ducked;
        public bool Airborne;
        public Vec3 TakeOff;
        public Vec3? LadderOn;
        public Vec3 LadderOff;
        /// <summary>A real player's movement since the last node, for <see cref="Recording"/>s.</summary>
        public readonly List<Sample> Trail = new();

        public void ResetMoves()
        {
            Rose = false;
            Ducked = false;
            Airborne = false;
            LadderOn = null;
        }
    }

    private readonly List<Vec3> _positions = new();
    private readonly List<List<Edge>> _edges = new();
    private readonly Dictionary<(int, int, int), List<int>> _cells = new();
    private readonly Dictionary<int, Walker> _walkers = new();
    private readonly Dictionary<(int From, int To), Recording> _recordings = new();

    // Search scratch, reused so a search on a big graph allocates nothing; see PathTree.
    private float[] _cost = [];
    private int[] _previous = [];
    private int[] _stamp = [];
    private int _generation;
    private readonly PriorityQueue<int, float> _queue = new();
    private readonly HashSet<int> _goalsLeft = new();

    public int Count => _positions.Count;
    public Vec3 Position(int node) => _positions[node];
    public IReadOnlyList<Edge> Edges(int node) => _edges[node];

    /// <summary>The node <paramref name="slot"/> was last recorded on, or -1.</summary>
    public int NodeOf(int slot) => _walkers.TryGetValue(slot, out var walker) ? walker.Node : -1;

    /// <summary>How a real player last took the edge from <paramref name="from"/> to <paramref name="to"/>, if one has (jumps and crouches only).</summary>
    public Recording? RecordingOf(int from, int to) => _recordings.GetValueOrDefault((from, to));

    public int Recordings => _recordings.Count;

    public void Clear()
    {
        _positions.Clear();
        _edges.Clear();
        _cells.Clear();
        _walkers.Clear();
        _recordings.Clear();
    }

    /// <summary>The player respawned, died or changed team: their next footstep starts afresh.</summary>
    public void Forget(int slot) => _walkers.Remove(slot);

    public void ForgetWalkers() => _walkers.Clear();

    /// <summary>
    /// Feeds a player's position. Footsteps on the ground become nodes; what happened in between
    /// (a jump, crouching, a ladder) goes on the edge to the next one. For a real player
    /// (<paramref name="real"/>, with the time and their velocity), how they took a jump or a crouch
    /// is kept as that edge's <see cref="Recording"/>.
    /// </summary>
    public void Record(int slot, Vec3 position, bool onGround, IWorld? world, bool ducked = false, bool onLadder = false,
        float now = float.NaN, Vec3 velocity = default, bool real = false)
    {
        if (!_walkers.TryGetValue(slot, out var walker)) _walkers[slot] = walker = new Walker();
        if (walker.LastSample is { } last && Vec3.Distance(last, position) > GapDistance)
        {
            walker.GapPending = true;
            walker.Trail.Clear();
        }
        walker.LastSample = position;
        walker.Ducked |= ducked;
        if (real && !float.IsNaN(now) && walker.Trail.Count < MaxTrail)
            walker.Trail.Add(new Sample(now, position, velocity, onGround && !onLadder, ducked));
        if (onLadder)
        {
            walker.LadderOn ??= position;
            walker.LadderOff = position;
            return;
        }
        if (!onGround)
        {
            if (!walker.Airborne)
            {
                walker.Airborne = true;
                walker.TakeOff = walker.LastGround;
            }
            if (walker.Node >= 0 && position.Z > walker.LastGround.Z + JumpRise) walker.Rose = true;
            return;
        }
        if (walker.Node >= 0 && !walker.GapPending && (position - walker.LastGround).Length2D > 4f)
            walker.Heading = (position - walker.LastGround).Normalized2D();

        var node = Find(position, MergeRadius, MergeHeight);
        if (node < 0)
        {
            if (walker.Node >= 0 && !walker.GapPending && walker.LadderOn is null && Vec3.Distance(_positions[walker.Node], position) < NodeSpacing)
            {
                // Still by the last node: whatever the player did there led nowhere new.
                walker.LastGround = position;
                walker.ResetMoves();
                walker.Ducked = ducked;
                Restart(walker);
                return;
            }
            if (Count >= MaxNodes) return;
            node = Add(position);
            if (world is not null) Link(node, world);
        }

        if (walker.Node >= 0 && node != walker.Node)
        {
            if (walker.GapPending)
            {
                AddEdge(walker.Node, new Edge(node, EdgeKind.Teleport, walker.LastGround + walker.Heading * GapWalkOn));
            }
            else if (walker.LadderOn is { } on)
            {
                AddEdge(walker.Node, new Edge(node, EdgeKind.Ladder, on));
                AddEdge(node, new Edge(walker.Node, EdgeKind.Ladder, walker.LadderOff));
            }
            else if (Connect(walker.Node, node, walker.Rose, walker.Ducked, walker.TakeOff) && (walker.Rose || walker.Ducked) && walker.Trail.Count >= 2)
            {
                _recordings[(walker.Node, node)] = new Recording(walker.Trail);
            }
        }
        walker.Node = node;
        walker.LastGround = position;
        walker.GapPending = false;
        walker.ResetMoves();
        walker.Ducked = ducked;
        Restart(walker);
    }

    /// <summary>The next edge's recording starts where the walker is now.</summary>
    private static void Restart(Walker walker)
    {
        if (walker.Trail.Count <= 1) return;
        var now = walker.Trail[^1];
        walker.Trail.Clear();
        walker.Trail.Add(now);
    }

    /// <summary>A node at <paramref name="position"/>, unlinked; the <see cref="Explorer"/> links it.</summary>
    public int Add(Vec3 position)
    {
        var node = _positions.Count;
        _positions.Add(position);
        _edges.Add(new List<Edge>());
        var key = Cell(position);
        if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = new List<int>();
        cell.Add(node);
        return node;
    }

    /// <summary>The closest node within the given distances, or -1.</summary>
    public int Find(Vec3 position, float radius, float height)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        var reach = (int)MathF.Ceiling(radius / CellSize);
        var (cx, cy, cz) = Cell(position);
        var reachZ = (int)MathF.Ceiling(height / CellSize);
        for (var x = cx - reach; x <= cx + reach; x++)
        for (var y = cy - reach; y <= cy + reach; y++)
        for (var z = cz - reachZ; z <= cz + reachZ; z++)
        {
            if (!_cells.TryGetValue((x, y, z), out var nodes)) continue;
            foreach (var node in nodes)
            {
                var other = _positions[node];
                var distance = Vec3.Distance2D(position, other);
                if (distance > radius || MathF.Abs(position.Z - other.Z) > height || distance >= bestDistance) continue;
                best = node;
                bestDistance = distance;
            }
        }
        return best;
    }

    public bool TryGetEdge(int from, int to, out Edge edge)
    {
        foreach (var candidate in _edges[from])
        {
            if (candidate.To != to) continue;
            edge = candidate;
            return true;
        }
        edge = default;
        return false;
    }

    /// <summary>
    /// Adds an edge, or keeps whichever of it and an existing edge between the same two nodes is
    /// easier (a walk beats a crouch beats a jump beats a ladder beats a teleport). Once a player
    /// has gone between the two nodes, the edge is no longer only <see cref="Edge.Explored"/>.
    /// </summary>
    public void AddEdge(int from, Edge edge)
    {
        var edges = _edges[from];
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i].To != edge.To) continue;
            var easier = edge.Difficulty < edges[i].Difficulty ? edge : edges[i];
            edges[i] = easier with { Explored = edges[i].Explored && edge.Explored };
            return;
        }
        edges.Add(edge);
    }

    /// <summary>The cost of taking an edge, before any per-bot scaling.</summary>
    public float Cost(int from, Edge edge)
    {
        if (edge.Kind == EdgeKind.Teleport) return TeleportCost;
        var cost = Vec3.WalkCost(_positions[from], _positions[edge.To]);
        if (edge.Explored) cost *= ExploredFactor;
        if (edge.Kind == EdgeKind.Ladder) return cost * LadderFactor;
        if ((edge.Moves & Moves.Crouch) != 0) cost *= CrouchFactor;
        if ((edge.Moves & Moves.Jump) != 0) cost += JumpCost;
        return cost;
    }

    /// <summary>
    /// Walking costs from <paramref name="start"/> to every node it can reach (Dijkstra).
    /// <paramref name="scale"/> multiplies an edge's cost (route variety; infinity skips the edge);
    /// <paramref name="penalty"/> adds to entering a node (crowding). With <paramref name="goals"/>,
    /// the search stops once all of them are settled, and it never goes past
    /// <paramref name="maxCost"/>; costs past either are then unknown. The result is only valid
    /// until the next search on this graph.
    /// </summary>
    public PathTree Search(
        int start,
        Func<int, int, float>? scale = null,
        Func<int, float>? penalty = null,
        IReadOnlyCollection<int>? goals = null,
        float maxCost = float.PositiveInfinity)
    {
        if (_cost.Length < Count)
        {
            var size = Math.Max(Count, _cost.Length * 2);
            _cost = new float[size];
            _previous = new int[size];
            _stamp = new int[size];
        }
        var generation = ++_generation;
        var tree = new PathTree(this, start, generation);
        if (start < 0 || start >= Count) return tree;

        _queue.Clear();
        _goalsLeft.Clear();
        if (goals is not null)
        {
            foreach (var goal in goals)
            {
                if (goal >= 0 && goal < Count) _goalsLeft.Add(goal);
            }
        }
        var stopAtGoals = goals is not null;
        Visit(start, 0f, -1);
        _queue.Enqueue(start, 0f);
        while (_queue.TryDequeue(out var node, out var reached))
        {
            // Stale entry: the node was reached more cheaply since.
            if (reached > _cost[node]) continue;
            if (reached > maxCost) break;
            if (stopAtGoals && _goalsLeft.Remove(node) && _goalsLeft.Count == 0) break;
            foreach (var edge in _edges[node])
            {
                var step = Cost(node, edge);
                if (scale is not null) step *= scale(node, edge.To);
                if (penalty is not null) step += penalty(edge.To);
                var total = reached + step;
                if (float.IsPositiveInfinity(total)) continue;
                if (_stamp[edge.To] == generation && total >= _cost[edge.To]) continue;
                Visit(edge.To, total, node);
                _queue.Enqueue(edge.To, total);
            }
        }
        return tree;

        void Visit(int node, float cost, int previous)
        {
            _stamp[node] = generation;
            _cost[node] = cost;
            _previous[node] = previous;
        }
    }

    internal float SearchCost(int node, int generation)
    {
        if (generation != _generation) throw new InvalidOperationException("A route search result was used after the next search on its graph.");
        return node >= 0 && node < _stamp.Length && _stamp[node] == generation ? _cost[node] : float.PositiveInfinity;
    }

    internal int SearchPrevious(int node) => _previous[node];

    /// <summary>Joins a new node to the nearby nodes it has a clear, level line to, both ways.</summary>
    private void Link(int node, IWorld world)
    {
        var position = _positions[node];
        var candidates = new List<(int Node, float Distance)>();
        var (cx, cy, cz) = Cell(position);
        var reach = (int)MathF.Ceiling(LinkRadius / CellSize);
        for (var x = cx - reach; x <= cx + reach; x++)
        for (var y = cy - reach; y <= cy + reach; y++)
        for (var z = cz - 1; z <= cz + 1; z++)
        {
            if (!_cells.TryGetValue((x, y, z), out var nodes)) continue;
            foreach (var other in nodes)
            {
                if (other == node) continue;
                var otherPosition = _positions[other];
                var distance = Vec3.Distance2D(position, otherPosition);
                if (distance <= LinkRadius && MathF.Abs(position.Z - otherPosition.Z) <= LinkHeight) candidates.Add((other, distance));
            }
        }
        foreach (var (other, _) in candidates.OrderBy(candidate => candidate.Distance).Take(MaxLinks))
        {
            if (!world.CanWalk(position, _positions[other])) continue;
            AddEdge(node, new Edge(other, EdgeKind.Walk, default));
            AddEdge(other, new Edge(node, EdgeKind.Walk, default));
        }
    }

    /// <summary>
    /// A step someone took: always possible forward, and back unless it was a drop too high to
    /// climb. A jump up is a drop back down; a jump across is a jump back too. False when the step
    /// was no step anyone can take (carried up higher than a jump).
    /// </summary>
    private bool Connect(int from, int to, bool jumped, bool ducked, Vec3 takeOff)
    {
        var rise = _positions[to].Z - _positions[from].Z;
        if (rise > MaxJumpRise) return false;
        var moves = (jumped ? Moves.Jump : Moves.None) | (ducked ? Moves.Crouch : Moves.None);
        AddEdge(from, new Edge(to, EdgeKind.Walk, jumped ? takeOff : default, moves));
        if (-rise > MaxClimb) return true;
        var back = jumped && rise > JumpRise ? moves & ~Moves.Jump : moves;
        AddEdge(to, new Edge(from, EdgeKind.Walk, (back & Moves.Jump) != 0 ? _positions[to] : default, back));
        return true;
    }

    private static (int, int, int) Cell(Vec3 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Y / CellSize), (int)MathF.Floor(position.Z / CellSize));
}

/// <summary>
/// The result of <see cref="WalkGraph.Search"/>: cost to and route to every reached node. It reads
/// the graph's search scratch, so it is only good until the next search on that graph.
/// </summary>
public sealed class PathTree
{
    private readonly WalkGraph _graph;
    private readonly int _generation;

    internal PathTree(WalkGraph graph, int start, int generation)
    {
        _graph = graph;
        Start = start;
        _generation = generation;
    }

    public int Start { get; }

    public bool Reached(int node) => !float.IsPositiveInfinity(Cost(node));

    public float Cost(int node) => _graph.SearchCost(node, _generation);

    /// <summary>Nodes from the start to <paramref name="node"/>, both included; empty when unreachable.</summary>
    public List<int> PathTo(int node)
    {
        var path = new List<int>();
        if (!Reached(node)) return path;
        for (var at = node; at >= 0; at = _graph.SearchPrevious(at)) path.Add(at);
        path.Reverse();
        return path;
    }
}
