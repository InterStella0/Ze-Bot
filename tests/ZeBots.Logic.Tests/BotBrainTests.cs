using ZeBots.Logic;
using static ZeBots.Logic.Tests.Walks;

namespace ZeBots.Logic.Tests;

public class BotBrainTests
{
    private static readonly BrainSettings Settings = new() { Aim = AimSkill.ForDifficulty(3) with { Jitter = 0f } };

    private static Agent Bot(int slot, Role role, Vec3 position, float yaw = 0f, ulong spottedBy = 0) =>
        new(slot, true, role, position, true, yaw, 0f, spottedBy);

    private static Agent Player(int slot, Role role, Vec3 position, ulong spottedBy = 0) =>
        new(slot, false, role, position, true, 0f, 0f, spottedBy);

    private static ulong SeenBy(int slot) => 1UL << slot;

    /// <summary>One map for a test: the learned routes, the walls, and the horde's routes.</summary>
    private sealed class Scene(IWorld? world = null)
    {
        public WalkGraph Graph { get; } = new();
        public IWorld? World { get; } = world;
        public Crowd Crowd { get; } = new();

        /// <summary>Records where everyone stands, as the plugin does each pass, then thinks.</summary>
        public BotCommand Think(BotState state, Agent self, IReadOnlyList<Agent> agents, float now = 1f, BrainSettings? settings = null, Ping? ping = null)
        {
            foreach (var agent in agents.Append(self)) Graph.Record(agent.Slot, agent.Position, agent.OnGround, World);
            return BotBrain.Think(state, self, agents, new Navigation(Graph, World, Crowd) { Ping = ping }, now, settings ?? Settings, new Random(7));
        }

        /// <summary>Thinks repeatedly so the view has time to turn.</summary>
        public BotCommand Settle(BotState state, Agent self, IReadOnlyList<Agent> agents)
        {
            var command = default(BotCommand);
            var bot = self;
            for (var i = 0; i < 20; i++)
            {
                command = Think(state, bot, agents.Select(agent => agent.Slot == self.Slot ? bot : agent).ToList(), 1f + i * 0.05f);
                bot = bot with { Yaw = command.Yaw, Pitch = command.Pitch };
            }
            return command;
        }

        /// <summary>Which way (north +1, south -1) the bot's route goes around the middle of the map.</summary>
        public int Side(BotState state)
        {
            if (state.Path.Any(node => Graph.Position(node).Y > 200f)) return 1;
            if (state.Path.Any(node => Graph.Position(node).Y < -200f)) return -1;
            return 0;
        }
    }

    /// <summary>Two routes of equal length from (0, 0) to (1000, 0), one north and one south.</summary>
    private static void TwoRoutes(Scene scene)
    {
        scene.Graph.Walk(20, scene.World, V(0, 0), V(0, 300), V(1000, 300), V(1000, 0));
        scene.Graph.Walk(21, scene.World, V(0, 0), V(0, -300), V(1000, -300), V(1000, 0));
    }

    [Fact]
    public void ZombieWithNoHumansIsHandedBackToNativeAi()
    {
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        Assert.False(new Scene().Think(new BotState(), zombie, [zombie]).Controlled);
    }

    [Fact]
    public void ZombieRunsStraightAtAHumanItHasAClearLineTo()
    {
        var zombie = Bot(1, Role.Zombie, Vec3.Zero, yaw: 0f);
        var human = Player(2, Role.Human, V(500, 0));
        var state = new BotState();

        var command = new Scene(TestWorld.Open()).Think(state, zombie, [zombie, human]);

        Assert.True(command.Controlled);
        Assert.True(state.Direct);
        Assert.Equal(1f, command.Forward, 2);
        Assert.Equal(0f, command.Left, 2);
        Assert.False(command.Attack);
    }

    [Fact]
    public void ZombieKnifesInsideKnifeRange()
    {
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        var human = Player(2, Role.Human, V(50, 0));

        Assert.True(new Scene(TestWorld.Open()).Think(new BotState(), zombie, [zombie, human]).Attack);
    }

    [Fact]
    public void ZombieGoesAroundAWallAlongTheRouteTheHumanTook()
    {
        // The human went north, east past the end of a wall, and back south to stand behind it.
        var scene = new Scene(TestWorld.Open().Wall(250, -500, 250, 300));
        scene.Graph.Walk(2, scene.World, V(0, 0), V(0, 400), V(500, 400), V(500, 0));
        var zombie = Bot(1, Role.Zombie, V(0, -10), yaw: 90f);
        var human = Player(2, Role.Human, V(500, 0));
        var state = new BotState();

        var command = scene.Think(state, zombie, [zombie, human]);

        // North along the route (cutting the corner where the line is clear), not east into the wall.
        Assert.False(state.Direct);
        Assert.True(command.Move.Y > 0.5f);
        Assert.True(scene.World!.CanWalk(zombie.Position, zombie.Position + command.Move * 400f));
    }

    [Fact]
    public void ZombieWaitsWhenNoKnownRouteReachesTheHuman()
    {
        var scene = new Scene(TestWorld.Open().Wall(250, -1000, 250, 1000));
        scene.Graph.Walk(2, scene.World, V(500, -200), V(500, 200));
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        var human = Player(2, Role.Human, V(500, 200));

        var command = scene.Think(new BotState(), zombie, [zombie, human]);

        Assert.True(command.Controlled);
        Assert.Equal(Vec3.Zero, command.Move);
    }

    [Fact]
    public void ZombieChoosesTheHumanNearestOnFoot()
    {
        // The human just across the wall is 200 away in a straight line but a long walk around it.
        var scene = new Scene(TestWorld.Open().Wall(-1000, 100, 1000, 100));
        scene.Graph.Walk(2, scene.World, V(0, 0), V(1100, 0), V(1100, 200), V(0, 200));
        scene.Graph.Walk(3, scene.World, V(0, 0), V(-600, 0));
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        var state = new BotState();

        scene.Think(state, zombie, [zombie, Player(2, Role.Human, V(0, 200)), Player(3, Role.Human, V(-600, 0))]);

        Assert.Equal(3, state.TargetSlot);
    }

    [Fact]
    public void ZombiePrefersHumansItCanSee()
    {
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        var near = Player(2, Role.Human, V(0, 500));
        var far = Player(3, Role.Human, V(-700, 0), spottedBy: SeenBy(1));
        var state = new BotState();

        new Scene().Think(state, zombie, [zombie, near, far]);

        // 700 * 0.6 (visible) beats 500 (hidden).
        Assert.Equal(3, state.TargetSlot);
    }

    [Fact]
    public void ZombieKeepsItsTargetUnlessAnotherIsClearlyCloser()
    {
        var scene = new Scene();
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        var first = Player(2, Role.Human, V(500, 0));
        var second = Player(3, Role.Human, V(-450, 0));
        var state = new BotState();
        scene.Think(state, zombie, [zombie, first]);
        Assert.Equal(2, state.TargetSlot);

        scene.Think(state, zombie, [zombie, first, second], now: 1.5f);
        Assert.Equal(2, state.TargetSlot);

        scene.Think(state, zombie, [zombie, first, second with { Position = V(-200, 0) }], now: 2f);
        Assert.Equal(3, state.TargetSlot);
    }

    [Fact]
    public void ZombiesSpreadOverTheRoutesToTheSameHuman()
    {
        var scene = new Scene(TestWorld.Open().Box(200, -100, 800, 100));
        TwoRoutes(scene);
        var human = Player(10, Role.Human, V(1000, 0));
        var first = Bot(1, Role.Zombie, Vec3.Zero);
        var second = Bot(2, Role.Zombie, Vec3.Zero);
        var firstState = new BotState();
        var secondState = new BotState();

        scene.Think(firstState, first, [first, second, human]);
        scene.Think(secondState, second, [first, second, human]);

        Assert.NotEqual(0, scene.Side(firstState));
        Assert.Equal(-scene.Side(firstState), scene.Side(secondState));
    }

    [Fact]
    public void BotsWithDifferentPersonasPreferDifferentRoutes()
    {
        var sides = new HashSet<int>();
        for (var seed = 0; seed < 12; seed++)
        {
            var scene = new Scene(TestWorld.Open().Box(200, -100, 800, 100));
            TwoRoutes(scene);
            var zombie = Bot(1, Role.Zombie, Vec3.Zero);
            var state = new BotState(Persona.Roll(new Random(seed)));
            scene.Think(state, zombie, [zombie, Player(10, Role.Human, V(1000, 0))]);
            sides.Add(scene.Side(state));
        }
        Assert.Equal(new HashSet<int> { -1, 1 }, sides);
    }

    [Fact]
    public void StuckBotRoutesAroundTheWayItCannotGet()
    {
        // Something the traces do not see (a door that shut) holds the bot where it stands.
        var scene = new Scene();
        TwoRoutes(scene);
        var zombie = Bot(1, Role.Zombie, Vec3.Zero);
        var human = Player(10, Role.Human, V(1000, 0));
        var state = new BotState();
        scene.Think(state, zombie, [zombie, human], now: 0f);
        var before = scene.Side(state);

        for (var t = 0.1f; t <= 3f; t += 0.1f) scene.Think(state, zombie, [zombie, human], now: t);

        Assert.NotEqual(0, before);
        Assert.Equal(-before, scene.Side(state));
    }

    [Fact]
    public void TeammatesKeepTheirDistance()
    {
        var scene = new Scene(TestWorld.Open());
        var human = Player(10, Role.Human, V(1000, 0));
        var first = Bot(1, Role.Zombie, Vec3.Zero);
        var second = Bot(2, Role.Zombie, V(0, 20));

        var a = scene.Think(new BotState(), first, [first, second, human]);
        var b = scene.Think(new BotState(), second, [first, second, human]);

        Assert.True(a.Move.Y < -0.3f);
        Assert.True(b.Move.Y > 0.3f);
        Assert.True(a.Move.X > 0.5f && b.Move.X > 0.5f);
    }

    [Fact]
    public void WaitingHumanBotsStepOutOfEachOther()
    {
        var scene = new Scene();
        var leader = Player(10, Role.Human, V(100, 0));
        var first = Bot(1, Role.Human, Vec3.Zero);
        var second = Bot(2, Role.Human, V(0, 10));

        var a = scene.Think(new BotState(), first, [first, second, leader]);
        var b = scene.Think(new BotState(), second, [first, second, leader]);

        Assert.True(a.Move.Y < 0f);
        Assert.True(b.Move.Y > 0f);
        Assert.True(a.Move.Length2D <= 0.5f + 1e-4f);
    }

    /// <summary>The human (slot 2) walked north and was teleported far east, then walked on east.</summary>
    private static void TeleporterRoute(Scene scene, Vec3 end)
    {
        scene.Graph.Walk(2, null, V(0, 0), V(0, 100));
        scene.Graph.Teleported(2, null, V(4000, 0));
        scene.Graph.Walk(2, null, V(4000, 0), end);
    }

    [Fact]
    public void AtATeleporterTheBotWalksOnIntoIt()
    {
        // The human walked north and was teleported far east.
        var scene = new Scene();
        TeleporterRoute(scene, V(4100, 0));
        var human = Player(2, Role.Human, V(4150, 0));
        var state = new BotState();

        var command = scene.Think(state, Bot(1, Role.Zombie, V(0, 90), yaw: 90f), [human]);

        // No teleport: keeps walking north past the last footstep, where the teleporter is.
        Assert.Null(command.TeleportTo);
        Assert.Equal(TeleportReason.None, command.Reason);
        Assert.Equal(V(0, 1), command.Move);
    }

    [Fact]
    public void PicksTheRouteUpAgainOnceTheMapCarriedTheBotAcross()
    {
        var scene = new Scene();
        TeleporterRoute(scene, V(4200, 0));
        var human = Player(2, Role.Human, V(4250, 0));
        var state = new BotState();
        scene.Think(state, Bot(1, Role.Zombie, V(0, 90)), [human]);

        // The map's teleporter dropped the bot near the far side.
        var command = scene.Think(state, Bot(1, Role.Zombie, V(3990, 20)), [human], now: 1.1f);

        Assert.Null(command.TeleportTo);
        Assert.True(command.Move.X > 0.9f);
    }

    [Fact]
    public void TeleporterIsCrossedByTeleportWhenCatchupIsOn()
    {
        var scene = new Scene();
        TeleporterRoute(scene, V(4100, 0));
        var human = Player(2, Role.Human, V(4150, 0));

        var command = scene.Think(new BotState(), Bot(1, Role.Zombie, V(0, 90)), [human], settings: Settings with { CatchupSeconds = 15f });

        Assert.Equal(V(4000, 0), command.TeleportTo);
        Assert.Equal(TeleportReason.Gap, command.Reason);
    }

    /// <summary>A zombie that never gets anywhere, on a straight route to a human 1000 units east.</summary>
    private static BotCommand ThinkUntilTeleport(int slot, float catchupSeconds)
    {
        var scene = new Scene();
        scene.Graph.Walk(30, null, V(0, 0), V(1000, 0));
        var bot = Bot(slot, Role.Zombie, Vec3.Zero);
        var human = Player(30, Role.Human, V(1020, 0));
        var state = new BotState();
        var settings = Settings with { CatchupSeconds = catchupSeconds };
        BotCommand command = default;
        for (var t = 0f; t <= 31f; t += 0.5f)
        {
            command = scene.Think(state, bot, [bot, human], t, settings);
            if (command.TeleportTo is not null) break;
        }
        return command;
    }

    [Fact]
    public void NeverTeleportsByDefault()
    {
        Assert.Null(ThinkUntilTeleport(0, catchupSeconds: 0f).TeleportTo);
    }

    [Fact]
    public void StuckBotTeleportsAFewNodesOnAlongItsRoute()
    {
        var command = ThinkUntilTeleport(0, catchupSeconds: 2f);

        Assert.Equal(TeleportReason.Stuck, command.Reason);
        Assert.Equal(144f, command.TeleportTo!.Value.X, 0);
    }

    [Fact]
    public void BotsCaughtUpTogetherLandOnDifferentPoints()
    {
        var spots = Enumerable.Range(0, 4).Select(slot => ThinkUntilTeleport(slot, catchupSeconds: 2f).TeleportTo).ToList();

        Assert.All(spots, spot => Assert.NotNull(spot));
        Assert.Equal(4, spots.Distinct().Count());
        Assert.All(spots, spot => Assert.InRange(spot!.Value.X, 128f, 500f));
    }

    [Fact]
    public void HumanBotWithNoRealPlayerIsHandedBackToNativeAi()
    {
        var human = Bot(1, Role.Human, Vec3.Zero);
        var otherBot = Bot(2, Role.Human, V(300, 0));
        Assert.False(new Scene().Think(new BotState(), human, [human, otherBot]).Controlled);
    }

    [Fact]
    public void HumanBotFollowsWithHysteresis()
    {
        var scene = new Scene();
        scene.Graph.Walk(2, null, V(0, 0), V(600, 0));
        var bot = Bot(1, Role.Human, Vec3.Zero);
        var state = new BotState();
        var leaderFar = Player(2, Role.Human, V(600, 0));
        Assert.True(scene.Think(state, bot, [bot, leaderFar]).Forward > 0.9f);
        Assert.True(state.Following);

        // Between the near and far distances: keeps walking.
        var leaderMid = leaderFar with { Position = V(300, 0) };
        Assert.True(scene.Think(state, bot, [bot, leaderMid], now: 1.6f).Forward > 0.9f);

        // Inside the near distance: stops, and stays stopped between the two.
        var leaderNear = leaderFar with { Position = V(150, 0) };
        Assert.Equal(0f, scene.Think(state, bot, [bot, leaderNear], now: 2.2f).Forward);
        Assert.False(state.Following);
        Assert.Equal(0f, scene.Think(state, bot, [bot, leaderMid], now: 2.8f).Forward);
    }

    [Fact]
    public void HumanBotShootsAVisibleZombieOnceAimed()
    {
        var scene = new Scene();
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 180f);
        var leader = Player(2, Role.Human, V(100, 0));
        var zombie = Bot(3, Role.Zombie, V(0, 1000), spottedBy: SeenBy(1));
        var state = new BotState();

        // Facing away at first: turns but holds fire.
        Assert.False(scene.Think(state, bot, [bot, leader, zombie]).Attack);

        var command = scene.Settle(state, bot, [bot, leader, zombie]);
        Assert.True(command.Attack);
        Assert.Equal(90f, command.Yaw, 0);
    }

    [Fact]
    public void HumanBotIgnoresZombiesItCannotSee()
    {
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 90f);
        var leader = Player(2, Role.Human, V(100, 0));
        var zombie = Bot(3, Role.Zombie, V(0, 1000));

        Assert.False(new Scene().Settle(new BotState(), bot, [bot, leader, zombie]).Attack);
    }

    [Fact]
    public void HumanBotBacksAwayFromACloseZombieWhileShooting()
    {
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 0f);
        var leader = Player(2, Role.Human, V(0, 100));
        var zombie = Bot(3, Role.Zombie, V(100, 0), spottedBy: SeenBy(1));

        var command = new Scene().Think(new BotState(), bot, [bot, leader, zombie]);

        // Facing the zombie (+X) and moving away from it.
        Assert.Equal(-1f, command.Forward, 2);
        Assert.True(command.Attack);
    }

    [Fact]
    public void HumanBotBackingOffStaysUpOnACrateItsLeaderIsOn()
    {
        // A crate 60 high from x = -60 east; the bot stands near its west edge with a zombie below.
        var world = TestWorld.Open().Platform(-60, -300, 400, 300, 60f);
        var bot = Bot(1, Role.Human, V(-30, 0, 60), yaw: 0f);
        var leader = Player(2, Role.Human, V(100, 150, 60));
        var zombie = Bot(3, Role.Zombie, V(100, 0, 0), spottedBy: SeenBy(1));

        var command = new Scene(world).Settle(new BotState(), bot, [bot, leader, zombie]);

        // Still backing off and shooting, but along the edge rather than off it.
        Assert.True(command.Attack);
        Assert.False(command.Move.IsZero);
        Assert.True(command.Move.X > -0.9f, $"{command.Move}");
        Assert.True(world.KeepsFooting(bot.Position, bot.Position + command.Move.Normalized2D() * 40f));
    }

    [Fact]
    public void HumanBotGoesOffTheCrateOnceItsLeaderHasDroppedDown()
    {
        var world = TestWorld.Open().Platform(-60, -300, 400, 300, 60f);
        var bot = Bot(1, Role.Human, V(-30, 0, 60), yaw: 0f);
        var leader = Player(2, Role.Human, V(-300, 0, 0));
        var zombie = Bot(3, Role.Zombie, V(100, 0, 0), spottedBy: SeenBy(1));

        var command = new Scene(world).Think(new BotState(), bot, [bot, leader, zombie]);

        Assert.True(command.Move.X < -0.9f, $"{command.Move}");
    }

    [Fact]
    public void WaitingHumanBotsMakingRoomOnALedgeDoNotStepOffIt()
    {
        // Up on a ledge whose south edge is at y = -20; a teammate right on top pushes the bot south.
        var world = TestWorld.Open().Platform(-300, -20, 300, 300, 60f);
        var scene = new Scene(world);
        var leader = Player(10, Role.Human, V(100, 100, 60));
        var first = Bot(1, Role.Human, V(0, 0, 60));
        var second = Bot(2, Role.Human, V(0, 10, 60));

        var command = scene.Think(new BotState(), first, [first, second, leader]);

        Assert.True(world.KeepsFooting(first.Position, first.Position + command.Move.Normalized2D() * 40f), $"{command.Move}");
    }

    [Fact]
    public void InfectedHumanBotSwitchesToChasing()
    {
        var scene = new Scene();
        var state = new BotState();
        var leader = Player(2, Role.Human, V(600, 0));
        scene.Think(state, Bot(1, Role.Human, Vec3.Zero), [leader]);
        Assert.Equal(Role.Human, state.Role);
        Assert.True(state.Following);

        var command = scene.Think(state, Bot(1, Role.Zombie, Vec3.Zero), [leader], now: 1.1f);

        Assert.Equal(Role.Zombie, state.Role);
        Assert.False(state.Following);
        Assert.Equal(2, state.TargetSlot);
        Assert.True(command.Controlled);
    }

    [Fact]
    public void MovesRelativeToTheViewTheBotActuallyHas()
    {
        // The bot looks along +Y while its target is along +X: it must strafe right, not walk forward,
        // even though it is being turned toward +X this pass.
        var zombie = Bot(1, Role.Zombie, Vec3.Zero, yaw: 90f);
        var human = Player(2, Role.Human, V(500, 0), spottedBy: SeenBy(1));

        var command = new Scene().Think(new BotState(), zombie, [zombie, human]);

        Assert.Equal(0f, command.Forward, 2);
        Assert.Equal(-1f, command.Left, 2);
        Assert.Equal(V(1, 0), command.Move);
    }

    [Fact]
    public void ZombieTakesAnExploredWayNoOneWalked()
    {
        // A wall between the zombie and the human, with a gap at its north end no one has been through.
        var scene = new Scene(TestWorld.Bounded(-480, -480, 480, 480).Wall(24, -500, 24, 300));
        var zombie = Bot(1, Role.Zombie, V(-240, 0));
        var human = Player(2, Role.Human, V(240, 0));
        scene.Graph.Record(zombie.Slot, zombie.Position, true, scene.World);
        scene.Graph.Record(human.Slot, human.Position, true, scene.World);
        scene.Graph.ExploreAll(scene.World!);
        var state = new BotState();

        var command = scene.Think(state, zombie, [zombie, human]);

        Assert.False(state.Direct);
        Assert.True(command.Move.Y > 0.3f);
        Assert.Contains(state.Path, node => scene.Graph.Position(node).Y > 300f);
    }

    [Fact]
    public void ZombieJumpsUpALedgeOnItsRoute()
    {
        var scene = new Scene(TestWorld.Bounded(-240, -96, 480, 96).Platform(200, -96, 480, 96, 40f));
        var zombie = Bot(1, Role.Zombie, V(192, 0));
        var human = Player(2, Role.Human, V(432, 0, 40));
        scene.Graph.Record(zombie.Slot, zombie.Position, true, scene.World);
        scene.Graph.Record(human.Slot, human.Position, true, scene.World);
        scene.Graph.ExploreAll(scene.World!);

        var command = scene.Think(new BotState(), zombie, [zombie, human]);

        Assert.True(command.Jump);
        Assert.True(command.Move.X > 0.5f);
    }

    [Fact]
    public void ZombieCrouchesThroughALowPassage()
    {
        var scene = new Scene(TestWorld.Bounded(-240, -24, 480, 24).LowCeiling(100, -24, 300, 24));
        var zombie = Bot(1, Role.Zombie, V(96, 0));
        var human = Player(2, Role.Human, V(432, 0));
        scene.Graph.Record(zombie.Slot, zombie.Position, true, scene.World);
        scene.Graph.Record(human.Slot, human.Position, true, scene.World);
        scene.Graph.ExploreAll(scene.World!);

        var command = scene.Think(new BotState(), zombie, [zombie, human]);

        Assert.True(command.Duck);
        Assert.False(command.Jump);
        Assert.True(command.Move.X > 0.5f);
    }

    /// <summary>The human (slot 2) walked east, climbed a ladder at x = 96 and walked on along the top.</summary>
    private static void LadderRoute(Scene scene)
    {
        scene.Graph.Walk(2, null, V(-96, 0), V(96, 0));
        scene.Graph.ClimbLadder(2, V(96, 0), V(120, 0, 256));
        scene.Graph.Walk(2, null, V(120, 0, 256), V(300, 0, 256));
    }

    [Fact]
    public void ZombieClimbsALadderUpAndStepsOffAtTheTop()
    {
        var scene = new Scene();
        LadderRoute(scene);
        var human = Player(2, Role.Human, V(300, 0, 256));
        var state = new BotState();

        // At the foot of the ladder: straight up it, holding on.
        var foot = scene.Think(state, Bot(1, Role.Zombie, V(96, 0)), [human]);
        Assert.Equal(BotBrain.ClimbSpeed, foot.Climb);
        Assert.True(foot.Move.Length2D < 0.1f);

        // Halfway up, in the air: still climbing.
        var halfway = scene.Think(state, Bot(1, Role.Zombie, V(96, 0, 128)) with { OnGround = false }, [human], now: 1.1f);
        Assert.Equal(BotBrain.ClimbSpeed, halfway.Climb);

        // Over the lip: off the ladder and onto the landing.
        var top = scene.Think(state, Bot(1, Role.Zombie, V(98, 0, 262)) with { OnGround = false }, [human], now: 1.2f);
        Assert.Equal(0f, top.Climb);
        Assert.True(top.Move.X > 0.5f);
    }

    [Fact]
    public void ZombieWalksToTheLadderBeforeClimbing()
    {
        var scene = new Scene();
        LadderRoute(scene);
        var human = Player(2, Role.Human, V(300, 0, 256));

        var command = scene.Think(new BotState(), Bot(1, Role.Zombie, V(-96, 0)), [human]);

        Assert.Equal(0f, command.Climb);
        Assert.True(command.Move.X > 0.9f);
    }

    [Fact]
    public void ZombieComesDownALadderNoFasterThanClimbing()
    {
        var scene = new Scene();
        LadderRoute(scene);
        // Someone else, waiting where the route started (the human who climbed is still up there).
        var human = Player(3, Role.Human, V(-96, 0));
        var state = new BotState();

        // On the landing: walk to the top of the ladder first.
        var landing = scene.Think(state, Bot(1, Role.Zombie, V(150, 0, 256)), [human]);
        Assert.Equal(0f, landing.Climb);
        Assert.True(landing.Move.X < -0.5f);

        // Over the edge: down the ladder.
        var down = scene.Think(state, Bot(1, Role.Zombie, V(100, 0, 200)) with { OnGround = false }, [human], now: 1.1f);
        Assert.Equal(-BotBrain.ClimbSpeed, down.Climb);
    }

    /// <summary>A player looking from where they stand at <paramref name="at"/>.</summary>
    private static Agent Aiming(Agent player, Vec3 at)
    {
        var (yaw, pitch) = Steering.LookAt(player.Eye, at);
        return player with { Yaw = yaw, Pitch = pitch };
    }

    [Fact]
    public void HumanBotShootsTheZombiePlayersAreFiringAt()
    {
        var bot = Bot(1, Role.Human, Vec3.Zero);
        var near = Bot(3, Role.Zombie, V(600, 0));
        var far = Bot(4, Role.Zombie, V(0, 1000));
        var leader = Aiming(Player(2, Role.Human, V(0, -100)), far.Chest) with { Firing = true };
        var state = new BotState();

        var command = new Scene(TestWorld.Open()).Settle(state, bot, [bot, leader, near, far]);

        // 1000 away but fired at (x 0.3) beats 600 away and ignored.
        Assert.Equal(far.Slot, state.AimSlot);
        Assert.True(command.Attack);
        Assert.Equal(90f, command.Yaw, 0);
    }

    [Fact]
    public void HumanBotShootsTheNearestZombieNoOneIsAimingAt()
    {
        var bot = Bot(1, Role.Human, Vec3.Zero);
        var near = Bot(3, Role.Zombie, V(600, 0));
        var far = Bot(4, Role.Zombie, V(0, 1000));
        var leader = Player(2, Role.Human, V(0, -100)) with { Yaw = 180f };
        var state = new BotState();

        new Scene(TestWorld.Open()).Settle(state, bot, [bot, leader, near, far]);

        Assert.Equal(near.Slot, state.AimSlot);
    }

    [Fact]
    public void HumanBotTurnsToShootAZombieBehindItThatItHasALineTo()
    {
        // The engine has not spotted the zombie (it is behind the bot), but nothing is in between.
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 0f);
        var leader = Player(2, Role.Human, V(0, 100));
        var zombie = Bot(3, Role.Zombie, V(-800, 0));

        var command = new Scene(TestWorld.Open()).Settle(new BotState(), bot, [bot, leader, zombie]);

        Assert.True(command.Attack);
        Assert.Equal(180f, MathF.Abs(command.Yaw), 0);
    }

    [Fact]
    public void HumanBotDoesNotShootThroughWalls()
    {
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 0f);
        var leader = Player(2, Role.Human, V(0, 100));
        var zombie = Bot(3, Role.Zombie, V(-800, 0));
        var state = new BotState();

        var command = new Scene(TestWorld.Open().Wall(-400, -500, -400, 500)).Settle(state, bot, [bot, leader, zombie]);

        Assert.False(command.Attack);
        Assert.Equal(-1, state.AimSlot);
    }

    [Fact]
    public void HumanBotLooksWhereTheShootingIs()
    {
        // The leader fires north at something the bot cannot see.
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 0f);
        var leader = Player(2, Role.Human, V(100, 0)) with { Yaw = 90f, Firing = true };

        var command = new Scene(TestWorld.Open()).Settle(new BotState(), bot, [bot, leader]);

        Assert.False(command.Attack);
        Assert.Equal(90f, command.Yaw, 0);
    }

    [Fact]
    public void WaitingHumanBotWatchesWhereItsLeaderWatches()
    {
        var bot = Bot(1, Role.Human, Vec3.Zero, yaw: 0f);
        var leader = Player(2, Role.Human, V(100, 0)) with { Yaw = -90f };

        var command = new Scene(TestWorld.Open()).Settle(new BotState(), bot, [bot, leader]);

        Assert.Equal(-90f, command.Yaw, 0);
    }

    [Fact]
    public void HumanBotGoesToAPingAndHoldsIt()
    {
        var scene = new Scene();
        scene.Graph.Walk(2, null, V(800, 0), V(0, 0));
        var leader = Player(2, Role.Human, V(100, 0));
        var ping = new Ping(leader.Slot, V(800, 0), 1f);
        var state = new BotState();

        // The leader is close, so the bot would wait; the ping sends it east.
        var going = scene.Think(state, Bot(1, Role.Human, Vec3.Zero), [leader], now: 1f, ping: ping);
        Assert.True(going.Move.X > 0.9f);

        // There: it stays, though the leader is far behind now.
        var there = scene.Think(state, Bot(1, Role.Human, V(780, 0)), [leader], now: 5f, ping: ping);
        Assert.Equal(Vec3.Zero, there.Move);

        // Once the ping is over, back to the leader.
        var back = scene.Think(state, Bot(1, Role.Human, V(780, 0)), [leader], now: 1f + Settings.PingSeconds + 1f, ping: ping);
        Assert.True(back.Move.X < -0.9f);
    }

    [Fact]
    public void PingsFromBotsAndZombiesAreIgnored()
    {
        var scene = new Scene();
        scene.Graph.Walk(2, null, V(800, 0), V(0, 0));
        var leader = Player(2, Role.Human, V(100, 0));
        var otherBot = Bot(3, Role.Human, V(0, 100));
        var zombie = Player(4, Role.Zombie, V(2000, 0));

        var fromBot = scene.Think(new BotState(), Bot(1, Role.Human, Vec3.Zero), [leader, otherBot], ping: new Ping(otherBot.Slot, V(800, 0), 1f));
        var fromZombie = scene.Think(new BotState(), Bot(1, Role.Human, Vec3.Zero), [leader, zombie], ping: new Ping(zombie.Slot, V(800, 0), 1f));

        Assert.True(fromBot.Move.Length2D <= 0.5f);
        Assert.Equal(Vec3.Zero, fromZombie.Move);
    }

    [Fact]
    public void ZombieInsideAStandingHumanStaysAndStabs()
    {
        var zombie = Bot(1, Role.Zombie, V(5, 0), yaw: 37f);
        var human = Player(2, Role.Human, Vec3.Zero);

        var command = new Scene(TestWorld.Open()).Think(new BotState(), zombie, [zombie, human]);

        Assert.Equal(Vec3.Zero, command.Move);
        Assert.True(command.Attack);
        // Keeps the way it faces and swings down into them, at once.
        Assert.Equal(37f, command.Yaw, 1);
        Assert.Equal(45f, command.Pitch, 1);
    }

    [Fact]
    public void ZombieInsideAMovingHumanKeepsPaceInsteadOfJittering()
    {
        var scene = new Scene(TestWorld.Open());
        var human = Player(2, Role.Human, Vec3.Zero) with { Velocity = V(200, 0) };
        var state = new BotState();

        // Just ahead of the human, then just behind: the same way both times, at the human's pace.
        var ahead = scene.Think(state, Bot(1, Role.Zombie, V(4, 2)), [human], now: 1f);
        var behind = scene.Think(state, Bot(1, Role.Zombie, V(-4, -2)), [human], now: 1.05f);

        Assert.True(ahead.Attack && behind.Attack);
        Assert.Equal(0.8f, ahead.Move.X, 2);
        Assert.Equal(ahead.Move, behind.Move);
    }

    [Fact]
    public void HumanBotRightOnTopOfAZombieKeepsBackingOffTheSameWay()
    {
        var scene = new Scene();
        var leader = Player(2, Role.Human, V(0, 300));
        var state = new BotState();

        var first = scene.Think(state, Bot(1, Role.Human, Vec3.Zero), [leader, Bot(3, Role.Zombie, V(100, 0))], now: 1f);
        var inside = scene.Think(state, Bot(1, Role.Human, Vec3.Zero), [leader, Bot(3, Role.Zombie, V(-3, 1))], now: 1.05f);

        Assert.Equal(V(-1, 0), first.Move);
        Assert.Equal(first.Move, inside.Move);
    }

    /// <summary>
    /// A real player (slot 2) ran east and jumped up onto a ledge 48 units high (as
    /// <see cref="RecordingTests.LedgeJump"/>), then walked on along it.
    /// </summary>
    private static void RecordedLedge(Scene scene)
    {
        foreach (var sample in RecordingTests.LedgeJump().Samples)
            scene.Graph.Record(2, sample.Position, sample.OnGround, null, sample.Ducked, now: sample.Time, velocity: sample.Velocity, real: true);
        scene.Graph.Walk(2, null, RecordingTests.LedgeJump().End, V(600, 0, 48));
    }

    [Fact]
    public void BotReplaysThePlayersJumpAfterLandingShortFiveTimes()
    {
        var scene = new Scene();
        RecordedLedge(scene);
        var human = Player(2, Role.Human, V(600, 0, 48));
        var recording = RecordingTests.LedgeJump();
        // Where the player last stood before jumping, which is where the replay starts.
        var start = recording.Samples.Last(sample => sample.OnGround && sample.Position.Z == 0f).Position;
        var bot = Bot(1, Role.Zombie, start);
        var state = new BotState();

        var now = 1f;
        BotCommand command = default;
        for (var attempt = 0; attempt < BotBrain.MimicAfter; attempt++)
        {
            command = scene.Think(state, bot, [human], now);
            Assert.Null(command.Mimic);
            scene.Think(state, bot with { OnGround = false, Position = start + V(0, 0, 30) }, [human], now += 0.1f);
            now += 0.1f;
        }
        Assert.Equal(BotBrain.MimicAfter - 1, state.HopFailures);

        // Landing short the fifth time: now it goes the player's way.
        command = scene.Think(state, bot, [human], now);

        Assert.Equal(BotBrain.MimicAfter, state.HopFailures);
        Assert.NotNull(command.Mimic);
        Assert.Same(state.Mimic, command.Mimic);
        Assert.True(command.Move.X > 0.9f);
    }

    [Fact]
    public void ReplayStartsWhereThePlayerStarted()
    {
        var scene = new Scene();
        RecordedLedge(scene);
        var human = Player(2, Role.Human, V(600, 0, 48));
        var recording = RecordingTests.LedgeJump();
        var start = recording.Samples.Last(sample => sample.OnGround && sample.Position.Z == 0f).Position;
        // Ahead of the start, nearer the ledge.
        var bot = Bot(1, Role.Zombie, start + V(20, 0));
        var state = new BotState();

        var now = 1f;
        for (var attempt = 0; attempt <= BotBrain.MimicAfter; attempt++)
        {
            scene.Think(state, bot with { OnGround = false, Position = bot.Position + V(0, 0, 30) }, [human], now += 0.1f);
            scene.Think(state, bot, [human], now += 0.1f);
        }

        var command = scene.Think(state, bot, [human], now + 0.1f);

        // Back west to where the player started, not yet replaying.
        Assert.Null(command.Mimic);
        Assert.True(command.Move.X < -0.3f);
        Assert.Equal(0f, command.Move.Y, 2);
    }
}
