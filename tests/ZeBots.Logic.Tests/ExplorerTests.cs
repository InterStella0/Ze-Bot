using ZeBots.Logic;
using static ZeBots.Logic.Tests.Walks;

namespace ZeBots.Logic.Tests;

public class ExplorerTests
{
    private static List<int> Route(WalkGraph graph, Vec3 from, Vec3 to)
    {
        var start = graph.Find(from, 30f, 30f);
        var goal = graph.Find(to, 30f, 30f);
        Assert.True(start >= 0, $"no node at {from}");
        Assert.True(goal >= 0, $"no node at {to}");
        return graph.Search(start).PathTo(goal);
    }

    private static List<Edge> RouteEdges(WalkGraph graph, Vec3 from, Vec3 to)
    {
        var route = Route(graph, from, to);
        var edges = new List<Edge>();
        for (var i = 1; i < route.Count; i++)
        {
            Assert.True(graph.TryGetEdge(route[i - 1], route[i], out var edge));
            edges.Add(edge);
        }
        return edges;
    }

    [Fact]
    public void MapsARoomFromOneFootstep()
    {
        var world = TestWorld.Bounded(-240, -240, 240, 240);
        var graph = new WalkGraph();
        graph.Record(1, V(0, 0), onGround: true, world);

        graph.ExploreAll(world);

        // An 11 x 11 grid, every point linked to its neighbours.
        Assert.Equal(121, graph.Count);
        Assert.NotEmpty(Route(graph, V(0, 0), V(240, 240)));
        Assert.NotEmpty(Route(graph, V(-240, 240), V(240, -240)));
    }

    [Fact]
    public void FindsTheDoorNoOneWalkedThrough()
    {
        // A wall across the room with a gap at its north end.
        var world = TestWorld.Bounded(-480, -480, 480, 480).Wall(24, -500, 24, 300);
        var graph = new WalkGraph();
        graph.Record(1, V(-240, 0), onGround: true, world);

        graph.ExploreAll(world);

        var route = Route(graph, V(-240, 0), V(240, 0));
        Assert.NotEmpty(route);
        Assert.Contains(route, node => graph.Position(node).Y > 300f);
    }

    [Fact]
    public void ALedgeIsAJumpUpAndADropDown()
    {
        var world = TestWorld.Bounded(-240, -96, 480, 96).Platform(200, -96, 480, 96, 40f);
        var graph = new WalkGraph();
        graph.Record(1, V(0, 0), onGround: true, world);

        graph.ExploreAll(world);

        var up = RouteEdges(graph, V(0, 0), V(384, 0, 40));
        Assert.Single(up, edge => edge.Moves == Moves.Jump);
        var down = RouteEdges(graph, V(384, 0, 40), V(0, 0));
        Assert.All(down, edge => Assert.True(edge.Plain));
    }

    [Fact]
    public void AHighLedgeTakesACrouchJumpAndAWallIsNoWayUp()
    {
        var crate = TestWorld.Bounded(-240, -96, 480, 96).Platform(200, -96, 480, 96, 60f);
        var graph = new WalkGraph();
        graph.Record(1, V(0, 0), onGround: true, crate);
        graph.ExploreAll(crate);
        Assert.Contains(RouteEdges(graph, V(0, 0), V(384, 0, 60)), edge => edge.Moves == (Moves.Jump | Moves.Crouch));

        var wall = TestWorld.Bounded(-240, -96, 480, 96).Platform(200, -96, 480, 96, 100f);
        var walled = new WalkGraph();
        walled.Record(1, V(0, 0), onGround: true, wall);
        walled.ExploreAll(wall);
        Assert.True(walled.Find(V(384, 0, 100), 30f, 30f) < 0);
    }

    [Fact]
    public void ALowPassageIsCrouchedThrough()
    {
        var world = TestWorld.Bounded(-240, -24, 480, 24).LowCeiling(100, -24, 300, 24);
        var graph = new WalkGraph();
        graph.Record(1, V(0, 0), onGround: true, world);

        graph.ExploreAll(world);

        var edges = RouteEdges(graph, V(0, 0), V(432, 0));
        Assert.Contains(edges, edge => edge.Moves == Moves.Crouch);
        Assert.Contains(edges, edge => edge.Plain);
    }

    [Fact]
    public void FootstepsStartNewWavesAndKnownGroundIsNotProbedTwice()
    {
        var world = TestWorld.Bounded(-240, -240, 240, 240);
        var graph = new WalkGraph();
        var explorer = new Explorer(graph);
        graph.Record(1, V(0, 0), onGround: true, world);
        while (explorer.Step(world)) { }
        var probes = world.Probes;
        var nodes = graph.Count;

        // Walking around the mapped room adds nothing new to probe.
        graph.Walk(1, world, V(0, 0), V(200, 200));
        while (explorer.Step(world)) { }

        Assert.Equal(nodes, graph.Count);
        Assert.True(world.Probes - probes < 20, $"{world.Probes - probes} probes");
        // Each pair of neighbours is probed once, not from both sides.
        Assert.True(probes < nodes * 6, $"{probes} probes for {nodes} nodes");
    }
}
