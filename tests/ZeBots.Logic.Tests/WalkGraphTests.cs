using ZeBots.Logic;
using static ZeBots.Logic.Tests.Walks;

namespace ZeBots.Logic.Tests;

public class WalkGraphTests
{
    private static bool Walkable(WalkGraph graph, Vec3 from, Vec3 to)
    {
        var start = graph.Find(from, 40f, 40f);
        var goal = graph.Find(to, 40f, 40f);
        return start >= 0 && goal >= 0 && graph.Search(start).Reached(goal);
    }

    [Fact]
    public void AddsANodeEveryFewSteps()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(480, 0));

        Assert.InRange(graph.Count, 10, 11);
        Assert.True(Walkable(graph, V(0, 0), V(480, 0)));
        Assert.True(Walkable(graph, V(480, 0), V(0, 0)));
    }

    [Fact]
    public void FootstepsNextToANodeShareIt()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(480, 0));
        var nodes = graph.Count;

        graph.Walk(2, null, V(0, 10), V(480, 10));

        Assert.Equal(nodes, graph.Count);
    }

    [Fact]
    public void ADropIsOneWay()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0, 200), V(96, 0, 200));
        graph.Record(1, V(120, 0, 100), onGround: false, null);
        graph.Walk(1, null, V(144, 0, 0), V(240, 0, 0));

        Assert.True(Walkable(graph, V(0, 0, 200), V(240, 0, 0)));
        Assert.False(Walkable(graph, V(240, 0, 0), V(0, 0, 200)));
    }

    [Fact]
    public void StairsAreTwoWay()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0, 0), V(96, 0, 0), V(192, 0, 48), V(288, 0, 48));

        Assert.True(Walkable(graph, V(288, 0, 48), V(0, 0, 0)));
    }

    [Fact]
    public void ATeleportBecomesAnEdgeToWalkInto()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(0, 100));
        graph.Teleported(1, null, V(4000, 0));
        graph.Walk(1, null, V(4000, 0), V(4100, 0));

        var before = graph.Find(V(0, 96), 40f, 40f);
        var after = graph.Find(V(4000, 0), 40f, 40f);
        Assert.True(graph.TryGetEdge(before, after, out var edge));
        Assert.Equal(EdgeKind.Teleport, edge.Kind);
        // On north past the last footstep, where the trigger is.
        Assert.Equal(0f, edge.Via.X, 1);
        Assert.Equal(100f + WalkGraph.GapWalkOn, edge.Via.Y, 1);
        // One way only.
        Assert.False(graph.TryGetEdge(after, before, out _));
    }

    [Fact]
    public void ForgettingAWalkerStartsAFreshWalk()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(96, 0));
        graph.Forget(1);
        graph.Walk(1, null, V(4000, 0), V(4096, 0));

        Assert.False(Walkable(graph, V(0, 0), V(4096, 0)));
    }

    [Fact]
    public void RoutesWalkedSideBySideJoinWhereTheLineIsClear()
    {
        var open = new WalkGraph();
        open.Walk(1, TestWorld.Open(), V(0, 0), V(480, 0));
        open.Walk(2, TestWorld.Open(), V(0, 80), V(480, 80));
        Assert.True(Walkable(open, V(0, 0), V(480, 80)));

        var walled = new WalkGraph();
        var wall = TestWorld.Open().Wall(-100, 40, 600, 40);
        walled.Walk(1, wall, V(0, 0), V(480, 0));
        walled.Walk(2, wall, V(0, 80), V(480, 80));
        Assert.False(Walkable(walled, V(0, 0), V(480, 80)));

        // Without traces, only shared footsteps join routes.
        var blind = new WalkGraph();
        blind.Walk(1, null, V(0, 0), V(480, 0));
        blind.Walk(2, null, V(0, 80), V(480, 80));
        Assert.False(Walkable(blind, V(0, 0), V(480, 80)));
    }

    [Fact]
    public void SearchTakesTheCheaperWayAndSkipsInfiniteEdges()
    {
        var graph = new WalkGraph();
        // A loop: short way along the bottom, long way over the top.
        graph.Walk(1, null, V(0, 0), V(480, 0));
        graph.Forget(1);
        graph.Walk(2, null, V(0, 0), V(0, 960), V(480, 960), V(480, 0));
        var start = graph.Find(V(0, 0), 40f, 40f);
        var goal = graph.Find(V(480, 0), 40f, 40f);

        var shortWay = graph.Search(start).PathTo(goal);
        Assert.All(shortWay, node => Assert.True(graph.Position(node).Y < 1f));

        var second = graph.Find(V(48, 0), 40f, 40f);
        var around = graph.Search(start, (from, to) => (from, to) == (start, second) ? float.PositiveInfinity : 1f).PathTo(goal);
        Assert.Contains(around, node => graph.Position(node).Y > 900f);
    }

    [Fact]
    public void AJumpUpALedgeIsAJumpThereAndADropBack()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(96, 0));
        graph.Jump(1, V(96, 0), V(144, 0, 40));
        graph.Walk(1, null, V(144, 0, 40), V(240, 0, 40));

        var below = graph.Find(V(96, 0), 30f, 30f);
        var above = graph.Find(V(144, 0, 40), 30f, 30f);
        Assert.True(graph.TryGetEdge(below, above, out var up));
        Assert.Equal(Moves.Jump, up.Moves);
        Assert.Equal(96f, up.Via.X, 0);
        Assert.True(graph.TryGetEdge(above, below, out var down));
        Assert.Equal(Moves.None, down.Moves);
    }

    [Fact]
    public void ACrouchJumpKeepsTheTuck()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(96, 0));
        graph.Jump(1, V(96, 0), V(144, 0, 60), tucked: true);

        var below = graph.Find(V(96, 0), 30f, 30f);
        var above = graph.Find(V(144, 0, 60), 30f, 30f);
        Assert.True(graph.TryGetEdge(below, above, out var up));
        Assert.Equal(Moves.Jump | Moves.Crouch, up.Moves);
    }

    [Fact]
    public void CrouchingThroughAVentMarksItBothWays()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(96, 0));
        graph.WalkAs(1, null, ducked: true, V(96, 0), V(288, 0));

        var start = graph.Find(V(144, 0), 30f, 30f);
        var next = graph.Find(V(192, 0), 30f, 30f);
        Assert.True(graph.TryGetEdge(start, next, out var forward));
        Assert.Equal(Moves.Crouch, forward.Moves);
        Assert.True(graph.TryGetEdge(next, start, out var back));
        Assert.Equal(Moves.Crouch, back.Moves);
    }

    [Fact]
    public void ALadderRideIsALadderEdgeBothWays()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(96, 0));
        graph.ClimbLadder(1, V(96, 0), V(120, 0, 256));

        var foot = graph.Find(V(96, 0), 30f, 30f);
        var top = graph.Find(V(120, 0, 256), 30f, 30f);
        Assert.True(graph.TryGetEdge(foot, top, out var up));
        Assert.Equal(EdgeKind.Ladder, up.Kind);
        Assert.Equal(8f, up.Via.Z, 0);
        Assert.True(graph.TryGetEdge(top, foot, out var down));
        Assert.Equal(EdgeKind.Ladder, down.Kind);
        Assert.True(down.Via.Z > 200f);
    }

    [Fact]
    public void TheEasierWayBetweenTwoNodesWins()
    {
        var graph = new WalkGraph();
        var a = graph.Add(V(0, 0));
        var b = graph.Add(V(48, 0));
        graph.AddEdge(a, new Edge(b, EdgeKind.Walk, V(0, 0), Moves.Jump));
        graph.AddEdge(a, new Edge(b, EdgeKind.Walk, default));
        graph.AddEdge(a, new Edge(b, EdgeKind.Walk, V(0, 0), Moves.Crouch));

        Assert.True(graph.TryGetEdge(a, b, out var edge));
        Assert.True(edge.Plain);
    }

    [Fact]
    public void SearchStopsAtItsCostLimit()
    {
        var graph = new WalkGraph();
        graph.Walk(1, null, V(0, 0), V(2000, 0));
        var start = graph.Find(V(0, 0), 40f, 40f);

        var tree = graph.Search(start, maxCost: 500f);

        Assert.True(tree.Reached(graph.Find(V(432, 0), 40f, 40f)));
        Assert.False(tree.Reached(graph.Find(V(2000, 0), 40f, 40f)));
    }

    [Fact]
    public void BeingCarriedHighUpIsNoStep()
    {
        // A bot moved up a ladder without being on it, as the plugin drives bots, or a push.
        var graph = new WalkGraph();
        graph.Walk(2, null, V(0, 0), V(96, 0));
        graph.ClimbLadder(2, V(96, 0), V(120, 0, 256));
        graph.Walk(1, null, V(0, 0), V(96, 0));
        graph.Jump(1, V(96, 0), V(120, 0, 256));

        var foot = graph.Find(V(96, 0), 30f, 30f);
        var top = graph.Find(V(120, 0, 256), 30f, 30f);
        Assert.True(graph.TryGetEdge(foot, top, out var edge));
        Assert.Equal(EdgeKind.Ladder, edge.Kind);
    }

    [Fact]
    public void BotsPreferTheWaysPlayersWentOverOnesOnlyTracesFound()
    {
        var graph = new WalkGraph();
        var a = graph.Add(V(0, 0));
        var b = graph.Add(V(100, 0));
        var explored = new Edge(b, EdgeKind.Walk, default, Explored: true);
        graph.AddEdge(a, explored);
        Assert.Equal(100f * WalkGraph.ExploredFactor, graph.Cost(a, graph.Edges(a)[0]), 1);

        // A player walks it: no longer only explored.
        graph.AddEdge(a, new Edge(b, EdgeKind.Walk, default));
        Assert.False(graph.Edges(a)[0].Explored);
        Assert.Equal(100f, graph.Cost(a, graph.Edges(a)[0]), 1);
    }

    [Fact]
    public void ExploredEdgesStayWalkedOnceAPlayerWentThere()
    {
        var graph = new WalkGraph();
        var a = graph.Add(V(0, 0));
        var b = graph.Add(V(100, 0, 40));
        graph.AddEdge(a, new Edge(b, EdgeKind.Walk, V(0, 0), Moves.Jump));
        // The traces find an easier way between the same nodes: easier kept, still known to work.
        graph.AddEdge(a, new Edge(b, EdgeKind.Walk, default, Explored: true));

        Assert.True(graph.Edges(a)[0].Plain);
        Assert.False(graph.Edges(a)[0].Explored);
    }
}
