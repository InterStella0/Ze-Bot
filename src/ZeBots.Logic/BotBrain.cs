namespace ZeBots.Logic;

/// <summary>Why a bot is being teleported.</summary>
public enum TeleportReason
{
    None,
    /// <summary>The route crosses a teleporter (or a fast elevator) that no one can walk.</summary>
    Gap,
    /// <summary>The bot made no progress for the catch-up time.</summary>
    Stuck,
}

/// <summary>
/// One pass of input for a bot, applied by the plugin.
/// </summary>
/// <param name="Controlled">False hands the bot back to the native bot AI.</param>
/// <param name="Move">Horizontal world direction to go; its length (at most 1) is the fraction of
/// run speed. Zero to stand. Includes stuck strafing, wander and keeping apart from teammates.</param>
/// <param name="Forward">Usercmd forward move (-1..1), relative to the bot's current view.</param>
/// <param name="Left">Usercmd left move (-1..1), relative to the bot's current view.</param>
/// <param name="Yaw">New view yaw, already limited by the bot's turn rate.</param>
/// <param name="Pitch">New view pitch.</param>
/// <param name="Jump">Jump now (onto a ledge, across a gap, or to get unstuck).</param>
/// <param name="Duck">Crouch (a low passage, the tuck of a crouch-jump, or to get unstuck).</param>
/// <param name="TeleportTo">Catch-up teleport destination (feet position), if any.</param>
/// <param name="Climb">On a ladder: vertical speed, up (positive) or down; <see cref="Move"/> is
/// then the exact horizontal move (fraction of <see cref="BotBrain.LadderSideSpeed"/>), without
/// steering.</param>
/// <param name="Mimic">Replaying how a player took the route edge here: a drive that can set
/// velocity follows it every tick instead of <see cref="Move"/>, <see cref="Jump"/> and
/// <see cref="Duck"/> (which give the same at this pass, for drives that press keys).</param>
public readonly record struct BotCommand(
    bool Controlled,
    Vec3 Move,
    float Forward,
    float Left,
    float Yaw,
    float Pitch,
    bool Attack,
    bool Jump,
    bool Duck,
    Vec3? TeleportTo,
    TeleportReason Reason,
    float Climb = 0f,
    MimicRun? Mimic = null)
{
    public static readonly BotCommand Release = new(false, Vec3.Zero, 0f, 0f, 0f, 0f, false, false, false, null, TeleportReason.None);
}

/// <summary>What every bot shares in one pass: the learned map, the engine's traces, and the horde's routes.</summary>
/// <param name="World">Null when traces are unavailable: bots then trust the engine's spotted state
/// to see targets and never cut corners.</param>
/// <param name="Plans">Route searches allowed this pass, so a crowd of bots replanning at once
/// does not stall a server frame; a bot over budget keeps its route until a later pass. A bot
/// with no target at all always plans.</param>
/// <param name="BudgetMs">No more searches once this pass has spent this long (from when the
/// navigation was made).</param>
public sealed record Navigation(WalkGraph Graph, IWorld? World, Crowd Crowd, int Plans = int.MaxValue, double BudgetMs = double.PositiveInfinity)
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The last ping a real human made; human bots go there while it lasts.</summary>
    public Ping? Ping { get; init; }

    public int PlansLeft { get; private set; } = Plans;

    internal bool TakePlan()
    {
        if (PlansLeft <= 0 || _clock.Elapsed.TotalMilliseconds >= BudgetMs) return false;
        PlansLeft--;
        return true;
    }
}

/// <summary>A player pinged <paramref name="Position"/> at <paramref name="At"/>.</summary>
public readonly record struct Ping(int Slot, Vec3 Position, float At);

/// <summary>What ZeBots remembers about one bot between passes.</summary>
public sealed class BotState(Persona? persona = null)
{
    public Persona Persona { get; } = persona ?? Persona.Plain;
    public Role? Role { get; internal set; }
    /// <summary>Zombie target, or human bot leader; -1 when none.</summary>
    public int TargetSlot { get; internal set; } = -1;
    /// <summary>A human bot walks after its leader until inside its near distance, then waits until past its far one.</summary>
    public bool Following { get; internal set; }
    /// <summary>A zombie that has a clear line to its target and is running straight at it.</summary>
    public bool Direct { get; internal set; }
    public StuckDetector Stuck { get; } = new();
    public float LastThink { get; internal set; } = float.NaN;

    /// <summary>Graph nodes from where the bot was to its goal; <see cref="PathIndex"/> is the one it is walking to.</summary>
    public IReadOnlyList<int> Path => _path;
    public int PathIndex { get; internal set; }
    internal List<int> _path = new();
    internal float PlannedAt { get; set; } = float.NegativeInfinity;
    /// <summary>Walking cost to the target when the route was planned.</summary>
    internal float TargetCost { get; set; } = float.PositiveInfinity;
    internal float PulledAt { get; set; } = float.NegativeInfinity;
    internal int LineFor { get; set; } = -1;
    internal bool LineClear { get; set; }
    internal float LineCheckedAt { get; set; } = float.NegativeInfinity;
    internal float DirectBlockedUntil { get; set; } = float.NegativeInfinity;
    /// <summary>Route edges this bot could not get along (a door that shut, a prop), until when.</summary>
    internal Dictionary<(int From, int To), float> Blocked { get; } = new();
    internal float WanderAngle { get; set; }
    internal float JitterYaw { get; set; }
    internal float JitterPitch { get; set; }
    internal float JitterChosenAt { get; set; } = float.NegativeInfinity;

    /// <summary>The zombie a human bot is shooting at; -1 when none.</summary>
    public int AimSlot { get; internal set; } = -1;
    /// <summary>Whether each zombie was in sight, and when that was checked.</summary>
    internal Dictionary<int, (bool Clear, float At)> Sight { get; } = new();
    /// <summary>The ping a human bot is answering (its time), or NaN.</summary>
    internal float PingAt { get; set; } = float.NaN;
    /// <summary>Last way a human bot backed off, kept while right on top of the zombie.</summary>
    internal Vec3 Away { get; set; }

    /// <summary>The route edge the bot is trying to get along, and how often it has landed short of it.</summary>
    internal (int From, int To) HopEdge { get; set; } = (-1, -1);
    public int HopFailures { get; internal set; }
    internal bool WasAirborne { get; set; }
    /// <summary>Replaying a player's recording of the edge, and how many replays fell short.</summary>
    public MimicRun? Mimic { get; internal set; }
    public int MimicFailures { get; internal set; }

    internal void ResetRoute()
    {
        _path.Clear();
        PathIndex = 0;
        PlannedAt = float.NegativeInfinity;
        TargetCost = float.PositiveInfinity;
        LineFor = -1;
        Direct = false;
    }
}

/// <summary>
/// ZE behavior for one bot per pass. Zombies hunt humans over the routes players have walked,
/// running straight only at a human they have a clear line to; human bots follow the nearest real
/// player the same way, shoot zombies they can see, and back away from zombies that get close.
/// Every bot has its own <see cref="Persona"/>, keeps its distance from teammates, and zombies
/// spread over different routes to the same humans.
/// </summary>
public static class BotBrain
{
    /// <summary>Close enough to a route node to walk on to the next one.</summary>
    public const float ArriveRadius = 40f;
    public const float ArriveHeight = 72f;
    /// <summary>A bot this close to the node a teleporter leads to has been carried across.</summary>
    private const float GapPickupRadius = 160f;
    /// <summary>Arriving over a jump or ladder takes being on the node's level, not just near it.</summary>
    private const float LandHeight = 24f;
    /// <summary>A jump starts within this distance of its take-off point, or this close below the ledge.</summary>
    private const float TakeOffRadius = 32f;
    private const float LedgeRadius = 44f;
    /// <summary>Ladder climbing speed (as on a real ladder), and how fast the bot moves sideways on one.</summary>
    public const float ClimbSpeed = 200f;
    public const float LadderSideSpeed = 150f;
    /// <summary>A bot this close to a ladder's foot (or top) is on it.</summary>
    private const float LadderRadius = 20f;
    /// <summary>Route searches go at least this far, and further for far targets, but never the whole map for nothing.</summary>
    private const float MinSearchCost = 4000f;
    private const float SearchCostFactor = 6f;
    /// <summary>A candidate replaces the current target only when it is clearly closer.</summary>
    private const float Hysteresis = 1.3f;
    /// <summary>Visible humans look this much closer to a zombie.</summary>
    private const float VisibleFactor = 0.6f;
    /// <summary>A human no known route reaches still counts, but as this much further than it looks.</summary>
    private const float UnreachableFactor = 3f;
    private const float JitterInterval = 0.3f;
    /// <summary>A bot at the end of its route (or without one) plans again this often.</summary>
    private const float IdleReplan = 0.25f;
    /// <summary>How long a line-of-sight check is reused.</summary>
    private const float LineInterval = 0.15f;
    /// <summary>Running straight at a target only happens this level with it.</summary>
    private const float DirectHeight = 64f;
    /// <summary>A bot stuck this long on a route edge gives up on it for a while and plans around it.</summary>
    private const float BlockAfter = 2.5f;
    private const float BlockFor = 8f;
    /// <summary>A bot stuck this long running straight at a target goes back to the route for a while.</summary>
    private const float DirectGiveUpAfter = 1.5f;
    private const float DirectGiveUpFor = 3f;
    /// <summary>Corner cutting: how far ahead along the route, how often, and how level.</summary>
    private const int PullAhead = 6;
    private const float PullInterval = 0.3f;
    private const float PullHeight = 32f;
    /// <summary>Teammates closer than this push each other apart.</summary>
    private const float SeparationRadius = 72f;
    /// <summary>A standing bot only steps aside when a teammate is this far into its space.</summary>
    private const float StandingPush = 0.4f;
    private const float StandingSpeed = 0.5f;
    /// <summary>How fast the wander angle drifts, in degrees per second.</summary>
    private const float WanderRate = 90f;
    /// <summary>
    /// Footing is checked this far ahead: about where a running bot that lets go of its keys stops.
    /// Blocked straight ahead, it tries these turns (degrees) to go along the edge instead.
    /// </summary>
    private const float FootingAhead = 40f;
    private static readonly float[] FootingTurns = [0f, 45f, -45f, 90f, -90f];
    /// <summary>Within this distance of its target a bot stops wandering.</summary>
    private const float WanderUntil = 300f;
    /// <summary>
    /// Teleporting onto the target itself would be unfair, so a catch-up lands a few route nodes
    /// ahead of where the bot is stuck; each bot adds a few more (by slot) so bots caught up together
    /// do not land on one spot.
    /// </summary>
    private const int CatchupNodesAhead = 2;
    private const int CatchupSpreadNodes = 2;
    /// <summary>A catch-up teleport shorter than this would not get the bot anywhere.</summary>
    private const float MinCatchupDistance = 128f;
    /// <summary>Closer than this (flat) a player is right on top of the other: directions to them mean nothing.</summary>
    private const float Overlap = 16f;
    /// <summary>A target moving slower than this is standing still.</summary>
    private const float StillSpeed = 50f;
    /// <summary>Run speed the target's speed is compared with, to keep pace with it.</summary>
    private const float PaceSpeed = 250f;
    /// <summary>
    /// A human bot shoots the zombie it sees that is cheapest by distance, and a zombie players
    /// are aiming at (within this angle) looks cheaper: more so when they are firing at it.
    /// </summary>
    private const float AttendAngle = 10f;
    private const float WatchedFactor = 0.6f;
    private const float FiredAtFactor = 0.3f;
    /// <summary>The zombie already being shot at looks this much cheaper, so aim does not flick between two.</summary>
    private const float KeepAimFactor = 0.75f;
    /// <summary>How long a line-of-sight check is good for, and how many new ones a bot makes a pass.</summary>
    private const float SightInterval = 0.2f;
    private const int SightChecksPerPass = 3;
    /// <summary>A real player's shots count this much more than a bot's toward where everyone looks.</summary>
    private const float RealShooterWeight = 2f;
    /// <summary>How far ahead the point a bot looks at along a direction is.</summary>
    private const float LookDistance = 512f;
    /// <summary>A human bot at a ping stops this close to it, and walks again past twice that.</summary>
    private const float PingArrive = 64f;
    /// <summary>The graph node for a ping is looked for this far around it.</summary>
    private const float PingSnap = 128f;
    /// <summary>
    /// A bot that lands short of a route edge this many times replays how a player took it, if one
    /// has (<see cref="WalkGraph.RecordingOf"/>), at most <see cref="MimicTries"/> times.
    /// </summary>
    public const int MimicAfter = 5;
    public const int MimicTries = 3;
    /// <summary>A replay starts once the bot stands this close to where the player started.</summary>
    private const float MimicStartRadius = 12f;

    public static BotCommand Think(
        BotState state,
        Agent self,
        IReadOnlyList<Agent> agents,
        Navigation nav,
        float now,
        BrainSettings settings,
        Random random)
    {
        var dt = float.IsNaN(state.LastThink) ? 0.03f : Math.Clamp(now - state.LastThink, 0f, 0.1f);
        state.LastThink = now;
        if (state.Role != self.Role)
        {
            // Infected (or revived): start over with the other role's goals.
            state.Role = self.Role;
            state.TargetSlot = -1;
            state.Following = false;
            state.ResetRoute();
            state.Stuck.Reset();
            nav.Crowd.Release(self.Slot);
        }

        var plan = self.Role == Role.Zombie
            ? PlanZombie(state, self, agents, nav, now, settings)
            : PlanHuman(state, self, agents, nav, now, settings);
        if (plan is not { } intent)
        {
            state.TargetSlot = -1;
            state.ResetRoute();
            state.Stuck.Reset();
            nav.Crowd.Release(self.Slot);
            return BotCommand.Release;
        }

        // A replay moves the bot exactly as a player went, back and forth included: not stuck.
        var wantsToMove = !intent.Move.IsZero && intent.Mimic is null;
        var stuck = state.Stuck.Update(self.Position, wantsToMove, now, settings.CatchupSeconds, random);
        var teleport = intent.Teleport;
        var reason = teleport is null ? TeleportReason.None : TeleportReason.Gap;
        if (teleport is null && stuck.Teleport)
        {
            if (intent.CatchupTo is { } to && Vec3.Distance(to, self.Position) >= MinCatchupDistance)
            {
                teleport = to;
                reason = TeleportReason.Stuck;
            }
            state.Stuck.Reset();
        }
        if (teleport is not null)
        {
            state.Stuck.Reset();
        }
        else if (wantsToMove)
        {
            GiveUpWhenStuck(state, nav.Graph, now);
        }

        // Aim: turn toward the look point at the bot's turn rate, with a slowly changing jitter.
        if (now - state.JitterChosenAt >= JitterInterval)
        {
            state.JitterChosenAt = now;
            state.JitterYaw = (random.NextSingle() * 2f - 1f) * settings.Aim.Jitter;
            state.JitterPitch = (random.NextSingle() * 2f - 1f) * settings.Aim.Jitter;
        }
        var (lookYaw, lookPitch) = Steering.LookAt(self.Eye, intent.LookAt);
        if (intent.Shoot)
        {
            lookYaw += state.JitterYaw;
            lookPitch += state.JitterPitch;
        }
        // A zombie in knife range turns at once: a player that close is swung at, not tracked.
        var (yaw, pitch) = intent.Snap
            ? (Steering.NormalizeYaw(lookYaw), Math.Clamp(lookPitch, -89f, 89f))
            : Steering.TurnToward(self.Yaw, self.Pitch, lookYaw, lookPitch, settings.Aim.TurnRate * dt);

        // On a ladder, or replaying a player, the move is exact: no drift, no making room for teammates.
        var move = intent.Exact ? intent.Move : Steer(state, self, agents, intent, stuck.StrafeLeft, dt, random);
        // Backing off, making room, wandering or getting unstuck never walks a bot off a ledge it got
        // up on: only going after someone (or a route) that is lower does.
        if (!intent.Exact && !intent.MayDrop && self.OnGround && !self.OnLadder && nav.World is { } world) move = KeepFooting(world, self, move);
        // Relative to the view the game has now, not the one just asked for: if the new view does not
        // take (or takes a tick late), the bot still walks the right way.
        var (forward, left) = Steering.ToLocal(move, self.Yaw);
        var speed = move.Length2D;

        var attack = intent.Shoot
            ? Steering.AngleError(yaw, pitch, lookYaw, lookPitch) <= settings.Aim.FireTolerance
            : intent.Attack;
        return new BotCommand(true, move, forward * speed, left * speed, yaw, pitch, attack,
            stuck.Jump || intent.Jump, stuck.Duck || intent.Duck, teleport, reason, intent.Climb, intent.Mimic);
    }

    /// <summary>What the bot wants this pass, before aim limits, stuck handling and steering.</summary>
    /// <param name="Move">Horizontal world direction to walk, or zero to stand.</param>
    /// <param name="LookAt">World point to look at.</param>
    /// <param name="Attack">Press attack regardless of aim (zombie knife).</param>
    /// <param name="Shoot">Press attack once the aim is on <paramref name="LookAt"/> (human gun).</param>
    /// <param name="Teleport">Teleport now (the route crosses a teleporter and catch-up is on).</param>
    /// <param name="CatchupTo">Where to teleport if the bot stays stuck too long.</param>
    /// <param name="Wander">Whether the bot may drift off the straight line (not when closing in).</param>
    /// <param name="Jump">Jump now (the route goes up a ledge or across a gap here).</param>
    /// <param name="Duck">Crouch (the route goes through a low passage, or this is a crouch-jump).</param>
    /// <param name="Climb">On a ladder: vertical speed; <paramref name="Move"/> is then exact.</param>
    /// <param name="Exact">Move exactly as <paramref name="Move"/> says (a ladder, a replay).</param>
    /// <param name="Mimic">Replaying how a player took the edge here.</param>
    /// <param name="Snap">Turn to <paramref name="LookAt"/> at once.</param>
    /// <param name="MayDrop">Going down is the point (the target or the route is lower): ledges are no reason to stop.</param>
    private readonly record struct Intent(
        Vec3 Move,
        Vec3 LookAt,
        bool Attack,
        bool Shoot,
        Vec3? Teleport,
        Vec3? CatchupTo,
        bool Wander,
        bool Jump = false,
        bool Duck = false,
        float Climb = 0f,
        bool Exact = false,
        MimicRun? Mimic = null,
        bool Snap = false,
        bool MayDrop = false);

    private static Intent Along(PathStep step, Vec3 move, Vec3 look, bool attack, bool shoot, Vec3? catchup, bool wander, bool mayDrop, bool snap = false) =>
        new(move, look, attack, shoot, step.Teleport, catchup, wander && step.Plain, step.Jump, step.Duck, step.Climb, step.Exact, step.Mimic, snap, mayDrop);

    /// <summary>Whether <paramref name="point"/> is further down than a step from <paramref name="self"/>.</summary>
    private static bool Below(Agent self, Vec3 point) => point.Z < self.Position.Z - Explorer.StepHeight;

    private static Intent? PlanZombie(BotState state, Agent self, IReadOnlyList<Agent> agents, Navigation nav, float now, BrainSettings settings)
    {
        var target = FindAgent(agents, state.TargetSlot);
        if (target is null || target.Role != Role.Human || (NeedsPlan(state, now) && nav.TakePlan()))
        {
            target = Plan(state, self, agents, nav, now, agent => agent.Role == Role.Human, crowd: true, visibleBonus: true);
            if (target is null) return null;
        }

        var distance = Vec3.Distance(self.Position, target.Position);
        var attack = distance <= settings.KnifeRange && MathF.Abs(self.Position.Z - target.Position.Z) <= DirectHeight;

        // Only a human the zombie has a clear line to is run at directly; anything else is reached
        // over the routes players have walked, never by pushing into a wall.
        state.Direct = now >= state.DirectBlockedUntil && distance <= settings.ChaseRange
            && MathF.Abs(self.Position.Z - target.Position.Z) <= DirectHeight && HasLine(state, self, target, nav, now);
        if (state.Direct)
        {
            return new Intent(Close(self, target), ZombieLook(self, target, attack), attack, false, null,
                CatchupAhead(state, nav.Graph, self.Slot), distance > WanderUntil, Snap: attack, MayDrop: Below(self, target.Position));
        }

        var step = FollowPath(state, self, nav, now, settings.CatchupSeconds > 0);
        if (step.Done)
        {
            // At the end of the route: the target walked from its last node to where it is now.
            var move = RouteEndsShort(state) ? Vec3.Zero : Close(self, target);
            return new Intent(move, ZombieLook(self, target, true), attack, false, null, null, false, Snap: attack, MayDrop: Below(self, target.Position));
        }
        var waypoint = step.Waypoint!.Value;
        var look = attack ? ZombieLook(self, target, true) : new Vec3(waypoint.X, waypoint.Y, self.Eye.Z);
        return Along(step, step.Move ?? (waypoint - self.Position).Normalized2D(), look, attack, false,
            CatchupAhead(state, nav.Graph, self.Slot), distance > WanderUntil, Below(self, waypoint) || Below(self, target.Position), snap: attack);
    }

    /// <summary>
    /// Straight at the target, or when already right on top of them, along with them (standing
    /// still with a target that stands): the direction to a player the bot is inside flips every
    /// pass, which would only make it jitter on the spot.
    /// </summary>
    private static Vec3 Close(Agent self, Agent target)
    {
        var offset = target.Position - self.Position;
        if (offset.Length2D >= Overlap) return offset.Normalized2D();
        var speed = target.Velocity.Length2D;
        if (speed < StillSpeed) return Vec3.Zero;
        return target.Velocity.Normalized2D() * MathF.Min(1f, speed / PaceSpeed);
    }

    /// <summary>
    /// Where a zombie looks at its target: the chest, or, right on top of them, ahead and down into
    /// them, keeping the way it faces (the direction to their middle means nothing there).
    /// </summary>
    private static Vec3 ZombieLook(Agent self, Agent target, bool close)
    {
        if (!close || Vec3.Distance2D(self.Position, target.Position) >= Overlap) return target.Chest;
        var facing = new Vec3(MathF.Cos(self.Yaw * (MathF.PI / 180f)), MathF.Sin(self.Yaw * (MathF.PI / 180f)), 0);
        return self.Eye + facing * Overlap + new Vec3(0, 0, -Overlap);
    }

    private static Intent? PlanHuman(BotState state, Agent self, IReadOnlyList<Agent> agents, Navigation nav, float now, BrainSettings settings)
    {
        static bool IsLeader(Agent agent) => agent.Role == Role.Human && !agent.IsBot;
        Agent? leader;
        float near, far, cost;
        Vec3 goal;
        if (ActivePing(nav, agents, now, settings) is { } ping)
        {
            // A real player pinged: go there and hold it while the ping lasts.
            leader = FindAgent(agents, ping.Slot)!;
            if (state.PingAt != ping.At || (NeedsPlan(state, now) && nav.TakePlan()))
            {
                state.PingAt = ping.At;
                state.TargetSlot = ping.Slot;
                PlanPoint(state, self, nav, now, ping.Position);
            }
            goal = ping.Position;
            near = PingArrive;
            far = PingArrive * 2f;
            cost = MathF.Abs(self.Position.Z - goal.Z) <= ArriveHeight ? Vec3.Distance2D(self.Position, goal) : float.PositiveInfinity;
        }
        else
        {
            leader = FindAgent(agents, state.TargetSlot);
            // Back from a ping: plan for the leader now.
            var fromPing = !float.IsNaN(state.PingAt);
            state.PingAt = float.NaN;
            if (leader is null || !IsLeader(leader) || fromPing || (NeedsPlan(state, now) && nav.TakePlan()))
            {
                leader = Plan(state, self, agents, nav, now, IsLeader, crowd: false, visibleBonus: false);
                if (leader is null) return null;
            }
            goal = leader.Position;
            // How far the leader is by foot: the planned route's cost, or straight when there is a clear line.
            cost = state.TargetCost;
            if (HasLine(state, self, leader, nav, now)) cost = MathF.Min(cost, Vec3.WalkCost(self.Position, leader.Position));
            if (float.IsPositiveInfinity(cost)) cost = Vec3.WalkCost(self.Position, leader.Position);
            far = MathF.Max(settings.FollowMax, settings.FollowMin) * state.Persona.FollowScale;
            near = settings.FollowMin * state.Persona.FollowScale;
        }
        if (cost > far) state.Following = true;
        else if (cost < near) state.Following = false;

        Agent? closest = null;
        var closestDistance = float.MaxValue;
        foreach (var agent in agents)
        {
            if (agent.Role != Role.Zombie) continue;
            var distance = Vec3.Distance(self.Position, agent.Position);
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closest = agent;
        }

        var move = Vec3.Zero;
        Vec3? waypoint = null;
        var step = new PathStep(null, null, true);
        if (closest is not null && closestDistance <= settings.RetreatRange * state.Persona.RetreatScale && !self.OnLadder)
        {
            move = Away(state, self, closest);
        }
        else if (state.Following)
        {
            step = FollowPath(state, self, nav, now, settings.CatchupSeconds > 0);
            waypoint = step.Waypoint;
            // At the end of the route the goal is close by: where the leader walked from their last node.
            if (step.Move is { } exact) move = exact;
            else if (waypoint is not null || !RouteEndsShort(state)) move = ((waypoint ?? goal) - self.Position).Normalized2D();
        }

        // Aim: a zombie in sight, else where everyone is shooting, else where the bot walks, else
        // where the leader looks.
        var shot = ChooseShot(state, self, agents, nav, now, settings);
        Vec3 look;
        if (shot is not null) look = shot.Chest;
        else if (Attention(self, agents) is { } direction) look = self.Eye + direction * LookDistance;
        else if (waypoint is { } point) look = new Vec3(point.X, point.Y, self.Eye.Z);
        else look = self.Eye + leader.Forward * LookDistance;
        // Off a ledge only after the leader (or the ping), or where the route goes down.
        var mayDrop = Below(self, goal) || (waypoint is { } next && Below(self, next));
        return Along(step, move, look, false, shot is not null, CatchupAhead(state, nav.Graph, self.Slot), waypoint is not null, mayDrop);
    }

    /// <summary>
    /// Away from the zombie; right on top of it, the way the bot was already backing off (or the way
    /// the zombie is going, out of its path), since the direction to its middle flips every pass.
    /// </summary>
    private static Vec3 Away(BotState state, Agent self, Agent zombie)
    {
        var offset = self.Position - zombie.Position;
        if (offset.Length2D >= Overlap || state.Away.IsZero)
        {
            var away = offset.Length2D >= 1f ? offset.Normalized2D() : zombie.Velocity.Normalized2D();
            if (!away.IsZero) state.Away = away;
        }
        return state.Away;
    }

    /// <summary>The last ping, while it lasts and the player who made it is still a living human.</summary>
    private static Ping? ActivePing(Navigation nav, IReadOnlyList<Agent> agents, float now, BrainSettings settings)
    {
        if (nav.Ping is not { } ping || now < ping.At || now - ping.At > settings.PingSeconds) return null;
        return FindAgent(agents, ping.Slot) is { Role: Role.Human, IsBot: false } ? ping : null;
    }

    /// <summary>
    /// The zombie a human bot should shoot: one it can see within range, nearest first, with the
    /// ones players are aiming (or firing) at counted nearer, and its current one kept unless
    /// another is clearly better.
    /// </summary>
    private static Agent? ChooseShot(BotState state, Agent self, IReadOnlyList<Agent> agents, Navigation nav, float now, BrainSettings settings)
    {
        Agent? best = null;
        var bestScore = float.MaxValue;
        var checks = 0;
        foreach (var zombie in agents.Where(agent => agent.Role == Role.Zombie).OrderBy(agent => Vec3.Distance(self.Eye, agent.Chest)))
        {
            var distance = Vec3.Distance(self.Eye, zombie.Chest);
            if (distance > settings.EngageRange) break;
            var score = distance * Attended(self, zombie, agents);
            if (zombie.Slot == state.AimSlot) score *= KeepAimFactor;
            if (score >= bestScore || !InSight(state, self, zombie, nav, now, ref checks)) continue;
            best = zombie;
            bestScore = score;
        }
        state.AimSlot = best?.Slot ?? -1;
        return best;
    }

    /// <summary>How much nearer a zombie seems because other humans are aiming or firing at it.</summary>
    private static float Attended(Agent self, Agent zombie, IReadOnlyList<Agent> agents)
    {
        var factor = 1f;
        var cos = MathF.Cos(AttendAngle * (MathF.PI / 180f));
        foreach (var human in agents)
        {
            if (human.Role != Role.Human || human.Slot == self.Slot || (human.IsBot && !human.Firing)) continue;
            var to = zombie.Chest - human.Eye;
            var length = to.Length;
            if (length < 1f || Dot(human.Forward, to) / length < cos) continue;
            factor = MathF.Min(factor, human.Firing ? FiredAtFactor : WatchedFactor);
        }
        return factor;
    }

    /// <summary>
    /// The way the humans who are shooting are aiming, on average (real players count more), or
    /// null when no one is shooting.
    /// </summary>
    private static Vec3? Attention(Agent self, IReadOnlyList<Agent> agents)
    {
        var sum = Vec3.Zero;
        foreach (var human in agents)
        {
            if (human.Role != Role.Human || human.Slot == self.Slot || !human.Firing) continue;
            sum += human.Forward * (human.IsBot ? 1f : RealShooterWeight);
        }
        var length = sum.Length;
        return length < 1e-3f ? null : sum * (1f / length);
    }

    /// <summary>
    /// Whether the bot can see the zombie: the engine says it does, or a trace from its eye to the
    /// zombie's chest is clear. Traces are reused for a moment, and only a few new ones are made a pass.
    /// </summary>
    private static bool InSight(BotState state, Agent self, Agent zombie, Navigation nav, float now, ref int checks)
    {
        if (zombie.IsSpottedBy(self.Slot)) return true;
        if (nav.World is null) return false;
        var known = state.Sight.TryGetValue(zombie.Slot, out var sight);
        if (known && now - sight.At < SightInterval) return sight.Clear;
        if (checks >= SightChecksPerPass) return known && sight.Clear;
        checks++;
        var clear = nav.World.CanSee(self.Eye, zombie.Chest);
        state.Sight[zombie.Slot] = (clear, now);
        return clear;
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Agent? FindAgent(IReadOnlyList<Agent> agents, int slot)
    {
        if (slot < 0) return null;
        foreach (var agent in agents)
        {
            if (agent.Slot == slot) return agent;
        }
        return null;
    }

    private static bool NeedsPlan(BotState state, float now)
    {
        var interval = state.PathIndex >= state._path.Count ? IdleReplan : state.Persona.Replan;
        return now - state.PlannedAt >= interval;
    }

    /// <summary>
    /// Searches the graph from the bot, picks the cheapest eligible agent by walking cost (as this
    /// bot's persona sees it), and sets the route to it. Zombies also route around each other.
    /// </summary>
    private static Agent? Plan(BotState state, Agent self, IReadOnlyList<Agent> agents, Navigation nav, float now, Func<Agent, bool> eligible, bool crowd, bool visibleBonus)
    {
        var graph = nav.Graph;
        var persona = state.Persona;
        if (crowd) nav.Crowd.Release(self.Slot);
        var start = NodeFor(graph, self);
        var scale = EdgeScale(state, now);
        Func<int, float>? penalty = crowd ? node => nav.Crowd.Penalty(graph.Position(node)) : null;
        var goals = agents.Where(agent => agent.Slot != self.Slot && eligible(agent)).Select(agent => NodeFor(graph, agent)).ToList();
        // Far enough for the winding route to the nearest candidate, but a target no route reaches
        // must not make every search cover the whole map.
        var nearest = agents.Where(agent => agent.Slot != self.Slot && eligible(agent))
            .Select(agent => Vec3.WalkCost(self.Position, agent.Position)).DefaultIfEmpty(0f).Min();
        var maxCost = MathF.Max(MinSearchCost, nearest * SearchCostFactor + MinSearchCost / 2f);
        var tree = start < 0 ? null : graph.Search(start, scale, penalty, goals, maxCost);

        float Cost(Agent agent)
        {
            var goal = NodeFor(graph, agent);
            var cost = tree is not null && tree.Reached(goal)
                ? tree.Cost(goal) + Vec3.Distance(graph.Position(goal), agent.Position)
                : Vec3.WalkCost(self.Position, agent.Position) * UnreachableFactor;
            if (visibleBonus && agent.IsSpottedBy(self.Slot)) cost *= VisibleFactor;
            return cost * persona.TargetFactor(agent.Slot);
        }

        Agent? best = null;
        Agent? current = null;
        var bestCost = float.MaxValue;
        var currentCost = float.MaxValue;
        foreach (var agent in agents)
        {
            if (agent.Slot == self.Slot || !eligible(agent)) continue;
            var cost = Cost(agent);
            if (agent.Slot == state.TargetSlot)
            {
                current = agent;
                currentCost = cost;
            }
            if (cost < bestCost)
            {
                bestCost = cost;
                best = agent;
            }
        }
        // Keep the current pick unless another is clearly cheaper, so bots do not flip every plan.
        var target = current is not null && currentCost <= bestCost * Hysteresis ? current : best;
        if (target is null) return null;
        if (target.Slot != state.TargetSlot)
        {
            state.TargetSlot = target.Slot;
            state.LineFor = -1;
            state.DirectBlockedUntil = float.NegativeInfinity;
        }

        state._path.Clear();
        state.PathIndex = 0;
        state.PlannedAt = now;
        state.TargetCost = float.PositiveInfinity;
        if (tree is not null)
        {
            var goal = NodeFor(graph, target);
            if (tree.Reached(goal))
            {
                state.TargetCost = tree.Cost(goal) + Vec3.Distance(graph.Position(goal), target.Position);
            }
            else
            {
                // No known way to the target: get as close as the known routes go.
                goal = ClosestReached(graph, tree, target.Position);
            }
            state._path.AddRange(tree.PathTo(goal));
            // The first node is where the bot already is.
            state.PathIndex = Math.Min(1, state._path.Count);
            if (crowd) nav.Crowd.Claim(self.Slot, state._path.Skip(state.PathIndex).Select(graph.Position));
        }
        return target;
    }

    /// <summary>This bot's route preferences, and the edges it has given up on for now.</summary>
    private static Func<int, int, float>? EdgeScale(BotState state, float now)
    {
        var persona = state.Persona;
        foreach (var edge in state.Blocked.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList()) state.Blocked.Remove(edge);
        if (state.Blocked.Count > 0) return (from, to) => state.Blocked.ContainsKey((from, to)) ? float.PositiveInfinity : persona.EdgeFactor(from, to);
        return persona.RouteNoise > 0 ? persona.EdgeFactor : null;
    }

    /// <summary>Sets the route to a point (a ping): to the node there, or as close as known routes go.</summary>
    private static void PlanPoint(BotState state, Agent self, Navigation nav, float now, Vec3 point)
    {
        var graph = nav.Graph;
        state._path.Clear();
        state.PathIndex = 0;
        state.PlannedAt = now;
        state.TargetCost = float.PositiveInfinity;
        var start = NodeFor(graph, self);
        if (start < 0) return;
        var goal = graph.Find(point, PingSnap, ArriveHeight);
        var maxCost = MathF.Max(MinSearchCost, Vec3.WalkCost(self.Position, point) * SearchCostFactor + MinSearchCost / 2f);
        var tree = graph.Search(start, EdgeScale(state, now), null, goal >= 0 ? [goal] : null, maxCost);
        if (goal >= 0 && tree.Reached(goal)) state.TargetCost = tree.Cost(goal) + Vec3.Distance(graph.Position(goal), point);
        else goal = ClosestReached(graph, tree, point);
        state._path.AddRange(tree.PathTo(goal));
        state.PathIndex = Math.Min(1, state._path.Count);
    }

    /// <summary>
    /// The route goes only as close to the target as known routes reach: at its end the bot waits
    /// rather than push into whatever is in between. (Without any route, a bot not yet on the graph
    /// heads straight for the target.)
    /// </summary>
    private static bool RouteEndsShort(BotState state) => state._path.Count > 0 && float.IsPositiveInfinity(state.TargetCost);

    private static int NodeFor(WalkGraph graph, Agent agent)
    {
        var node = graph.NodeOf(agent.Slot);
        return node >= 0 ? node : graph.Find(agent.Position, 128f, ArriveHeight);
    }

    private static int ClosestReached(WalkGraph graph, PathTree tree, Vec3 position)
    {
        var best = tree.Start;
        var bestCost = float.MaxValue;
        for (var node = 0; node < graph.Count; node++)
        {
            if (!tree.Reached(node)) continue;
            var cost = Vec3.WalkCost(graph.Position(node), position);
            if (cost >= bestCost) continue;
            bestCost = cost;
            best = node;
        }
        return best;
    }

    /// <summary>
    /// Whether the bot has a clear, walkable line to <paramref name="target"/>: the engine's trace
    /// when there is one, otherwise whether the engine says the bot can see it.
    /// </summary>
    private static bool HasLine(BotState state, Agent self, Agent target, Navigation nav, float now)
    {
        if (nav.World is null) return target.IsSpottedBy(self.Slot);
        if (state.LineFor == target.Slot && now - state.LineCheckedAt < LineInterval) return state.LineClear;
        state.LineFor = target.Slot;
        state.LineCheckedAt = now;
        state.LineClear = nav.World.CanWalk(self.Position, target.Position);
        return state.LineClear;
    }

    /// <param name="Move">Set when the move is not simply toward <paramref name="Waypoint"/> (on a ladder, in a replay).</param>
    /// <param name="Plain">The bot is on a plain walking edge (it may wander off the straight line).</param>
    /// <param name="Exact">Move exactly as <paramref name="Move"/> says, without steering.</param>
    private readonly record struct PathStep(
        Vec3? Waypoint,
        Vec3? Teleport,
        bool Done,
        Vec3? Move = null,
        bool Jump = false,
        bool Duck = false,
        float Climb = 0f,
        bool Plain = true,
        bool Exact = false,
        MimicRun? Mimic = null);

    /// <summary>
    /// Walks the bot along its route: advances as nodes are reached (or passed), cuts corners the
    /// world says are clear, jumps, crouches and climbs where the route needs it, and takes
    /// teleporters by walking on into them (or, with <paramref name="teleport"/>, by being
    /// teleported across).
    /// </summary>
    private static PathStep FollowPath(BotState state, Agent self, Navigation nav, float now, bool teleport)
    {
        var graph = nav.Graph;
        var path = state._path;
        for (var guard = 0; guard < 32 && state.PathIndex < path.Count; guard++)
        {
            var node = path[state.PathIndex];
            var position = graph.Position(node);
            var edge = default(Edge);
            var hasEdge = state.PathIndex > 0 && graph.TryGetEdge(path[state.PathIndex - 1], node, out edge);
            if (hasEdge && edge.Kind == EdgeKind.Teleport)
            {
                if (Near(self.Position, position, GapPickupRadius))
                {
                    state.PathIndex++;
                    continue;
                }
                if (teleport)
                {
                    state.PathIndex++;
                    return new PathStep(position, position, false);
                }
                return new PathStep(edge.Via, null, false);
            }

            var plain = !hasEdge || edge.Plain;
            // Over a ledge or up a ladder, being near the node is not enough: the bot must be up there.
            var arrived = plain
                ? Near(self.Position, position, ArriveRadius)
                : Vec3.Distance2D(self.Position, position) <= ArriveRadius && MathF.Abs(self.Position.Z - position.Z) <= LandHeight && self.OnGround;
            // Pushed or jumped past the node: closer to the next node than this one is.
            var passed = plain && state.PathIndex + 1 < path.Count
                && graph.TryGetEdge(node, path[state.PathIndex + 1], out var nextEdge) && nextEdge.Plain
                && Vec3.Distance2D(self.Position, graph.Position(nextEdge.To)) < Vec3.Distance2D(position, graph.Position(nextEdge.To))
                && MathF.Abs(self.Position.Z - graph.Position(nextEdge.To).Z) <= ArriveHeight;
            if (!arrived && !passed) break;
            state.PathIndex++;
        }
        if (state.PathIndex >= path.Count)
        {
            state.Mimic = null;
            return new PathStep(null, null, true);
        }

        var target = graph.Position(path[state.PathIndex]);
        if (state.PathIndex > 0 && graph.TryGetEdge(path[state.PathIndex - 1], path[state.PathIndex], out var current))
        {
            if (Mimic(state, self, graph, path[state.PathIndex - 1], path[state.PathIndex], now) is { } replay) return replay;
            if (!current.Plain)
            {
                var from = graph.Position(path[state.PathIndex - 1]);
                return current.Kind == EdgeKind.Ladder ? Climb(self, current, from, target) : Hop(self, current, target);
            }
        }
        CutCorners(state, self, nav, now);
        return new PathStep(graph.Position(path[state.PathIndex]), null, false);
    }

    /// <summary>
    /// Counts how often the bot lands short of the edge it is on. After <see cref="MimicAfter"/>
    /// times, if a player has been recorded taking the edge, the bot walks to where they started
    /// and does what they did (<see cref="MimicRun"/>), up to <see cref="MimicTries"/> times. Null
    /// while the bot should keep trying its own way.
    /// </summary>
    private static PathStep? Mimic(BotState state, Agent self, WalkGraph graph, int from, int to, float now)
    {
        if (state.HopEdge != (from, to))
        {
            state.HopEdge = (from, to);
            state.HopFailures = 0;
            state.MimicFailures = 0;
            state.Mimic = null;
            state.WasAirborne = false;
        }
        var landed = state.WasAirborne && self.OnGround;
        state.WasAirborne = !self.OnGround && !self.OnLadder;

        var end = graph.Position(to);
        if (state.Mimic is { } run)
        {
            if (!run.Over(now))
            {
                var drive = run.Peek(now, self.Position, self.OnGround);
                return new PathStep(end, null, false, MimicRun.AsMove(drive), drive.Jump, drive.Duck, Plain: false, Exact: true, Mimic: run);
            }
            // The replay ended without getting there (arriving moves the bot on to the next edge).
            state.Mimic = null;
            state.MimicFailures++;
        }
        else if (landed)
        {
            state.HopFailures++;
        }
        if (state.HopFailures < MimicAfter || state.MimicFailures >= MimicTries || graph.RecordingOf(from, to) is not { } recording) return null;

        // Stand where the player started, then go.
        var start = recording.Start;
        if (Vec3.Distance2D(self.Position, start) > MimicStartRadius || MathF.Abs(self.Position.Z - start.Z) > LandHeight || !self.OnGround)
            return new PathStep(start, null, false, Exact(self, start, 4f * MimicStartRadius), Plain: false, Exact: true);
        state.Mimic = new MimicRun(recording, now);
        var first = state.Mimic.Peek(now, self.Position, self.OnGround);
        return new PathStep(end, null, false, MimicRun.AsMove(first), first.Jump, first.Duck, Plain: false, Exact: true, Mimic: state.Mimic);
    }

    /// <summary>A walk edge that takes a jump, a crouch, or both.</summary>
    private static PathStep Hop(Agent self, Edge edge, Vec3 to)
    {
        var duck = (edge.Moves & Moves.Crouch) != 0;
        if ((edge.Moves & Moves.Jump) == 0) return new PathStep(to, null, false, Duck: duck, Plain: false);
        // Take off at the take-off point, or at the latest right below the ledge, already running.
        var jump = self.OnGround
            && (Vec3.Distance2D(self.Position, edge.Via) <= TakeOffRadius
                || (Vec3.Distance2D(self.Position, to) <= LedgeRadius && to.Z > self.Position.Z + Explorer.StepHeight));
        // A crouch-jump tucks the legs in the air; crouching on the ground first would only slow the run-up.
        return new PathStep(to, null, false, Jump: jump, Duck: duck && !self.OnGround, Plain: false);
    }

    /// <summary>
    /// A ladder: walk to its foot (or top), then go straight up (or down) it, and off at the other
    /// end. The plugin moves the bot along the ladder at ladder speed, since it cannot press keys.
    /// </summary>
    private static PathStep Climb(Agent self, Edge edge, Vec3 from, Vec3 to)
    {
        var mount = edge.Via;
        var flatToMount = new Vec3(mount.X - self.Position.X, mount.Y - self.Position.Y, 0);
        var onIt = flatToMount.Length2D <= LadderRadius || !self.OnGround || self.OnLadder;
        if (to.Z > from.Z)
        {
            // Over the lip: step off onto the landing.
            if (self.Position.Z >= to.Z + 4f) return new PathStep(to, null, false, Plain: false);
            if (!onIt) return new PathStep(mount, null, false, Plain: false);
            // Near the top lean toward the landing to get over the edge, otherwise hold onto the ladder.
            var lean = self.Position.Z >= to.Z - Agent.ChestHeight ? to : mount;
            return new PathStep(to, null, false, Exact(self, lean, 2f * LadderRadius), Climb: ClimbSpeed, Plain: false, Exact: true);
        }

        // Down: at the bottom, step off. From the top, go over the edge toward the foot of the
        // ladder and come down no faster than climbing.
        if (self.OnGround && self.Position.Z <= to.Z + LandHeight) return new PathStep(to, null, false, Plain: false);
        if (!onIt) return new PathStep(mount, null, false, Plain: false);
        return new PathStep(to, null, false, Exact(self, to, 2f * LadderRadius), Climb: -ClimbSpeed, Plain: false, Exact: true);
    }

    /// <summary>A horizontal move toward <paramref name="point"/> that eases off within <paramref name="ease"/> of it.</summary>
    private static Vec3 Exact(Agent self, Vec3 point, float ease)
    {
        var flat = new Vec3(point.X - self.Position.X, point.Y - self.Position.Y, 0);
        var distance = flat.Length2D;
        return distance < 1f ? Vec3.Zero : flat.Normalized2D() * MathF.Min(1f, distance / ease);
    }

    private static bool Near(Vec3 a, Vec3 b, float radius) =>
        Vec3.Distance2D(a, b) <= radius && MathF.Abs(a.Z - b.Z) <= ArriveHeight;

    /// <summary>Skips ahead to the furthest of the next few route nodes the bot can walk straight to.</summary>
    private static void CutCorners(BotState state, Agent self, Navigation nav, float now)
    {
        if (nav.World is null || now - state.PulledAt < PullInterval) return;
        state.PulledAt = now;
        var graph = nav.Graph;
        var path = state._path;
        var limit = state.PathIndex;
        for (var j = state.PathIndex + 1; j < path.Count && j <= state.PathIndex + PullAhead; j++)
        {
            if (!graph.TryGetEdge(path[j - 1], path[j], out var edge) || !edge.Plain) break;
            limit = j;
        }
        for (var j = limit; j > state.PathIndex; j--)
        {
            var position = graph.Position(path[j]);
            if (MathF.Abs(position.Z - self.Position.Z) > PullHeight) continue;
            if (!nav.World.CanWalk(self.Position, position)) continue;
            state.PathIndex = j;
            return;
        }
    }

    /// <summary>
    /// A bot that keeps failing to get somewhere stops trying that way for a while: a route edge (a
    /// door shut, something in the way), or a straight run at a target, for the route. An edge a
    /// player was recorded taking is kept until the bot has tried their way too.
    /// </summary>
    private static void GiveUpWhenStuck(BotState state, WalkGraph graph, float now)
    {
        if (state.Direct)
        {
            if (state.Stuck.StuckFor < DirectGiveUpAfter) return;
            state.DirectBlockedUntil = now + DirectGiveUpFor;
            state.PlannedAt = float.NegativeInfinity;
            state.Stuck.Reset();
            return;
        }
        if (state.Stuck.StuckFor < BlockAfter || state.PathIndex <= 0 || state.PathIndex >= state._path.Count) return;
        var edge = (state._path[state.PathIndex - 1], state._path[state.PathIndex]);
        if (state.HopEdge == edge && state.MimicFailures < MimicTries && graph.RecordingOf(edge.Item1, edge.Item2) is not null)
        {
            // Getting nowhere while already on the player's way (walking to where they started) is a failed try too.
            if (state.HopFailures >= MimicAfter) state.MimicFailures++;
            state.Stuck.Reset();
            return;
        }
        state.Blocked[edge] = now + BlockFor;
        state.PlannedAt = float.NegativeInfinity;
        state.Stuck.Reset();
    }

    /// <summary>
    /// For a bot stuck on its route: a node a little further along, past whatever holds it. How far
    /// depends on the slot, so bots stuck at one spot do not land on one spot.
    /// </summary>
    private static Vec3? CatchupAhead(BotState state, WalkGraph graph, int slot)
    {
        var path = state._path;
        if (path.Count == 0) return null;
        var index = Math.Min(path.Count - 1, state.PathIndex + CatchupNodesAhead + (slot % 4) * CatchupSpreadNodes);
        return graph.Position(path[index]);
    }

    /// <summary>
    /// Turns what the bot wants into how it moves: its own drift off the straight line, keeping
    /// apart from teammates (the boids' separation rule), sliding sideways when stuck, and its own
    /// pace.
    /// </summary>
    private static Vec3 Steer(BotState state, Agent self, IReadOnlyList<Agent> agents, Intent intent, float strafeLeft, float dt, Random random)
    {
        var persona = state.Persona;
        var apart = Separation(self, agents);
        // Slower than a full run when asked (keeping pace with a target).
        var pace = MathF.Min(1f, intent.Move.Length2D);
        var move = intent.Move.Normalized2D();
        if (move.IsZero)
        {
            // Standing: only step out of a teammate who is right on top of the bot.
            return apart.Length2D >= StandingPush ? apart.Normalized2D() * (StandingSpeed * persona.Speed) : Vec3.Zero;
        }

        if (intent.Wander && persona.Wander > 0f)
        {
            state.WanderAngle = Math.Clamp(state.WanderAngle + (random.NextSingle() * 2f - 1f) * WanderRate * dt, -persona.Wander, persona.Wander);
            move = Rotate(move, state.WanderAngle);
        }
        else
        {
            state.WanderAngle = 0f;
        }
        move = (move + apart * persona.Separation).Normalized2D();
        if (move.IsZero) move = intent.Move.Normalized2D();

        // A stuck bot slides sideways (relative to where it is heading) to get off what blocks it.
        if (strafeLeft != 0f)
        {
            var side = new Vec3(-move.Y, move.X, 0) * strafeLeft;
            move = (move + side).Normalized2D();
        }
        return move * (persona.Speed * pace);
    }

    /// <summary>
    /// <paramref name="move"/>, or the nearest way round it that does not walk off a ledge (along the
    /// edge), or standing still.
    /// </summary>
    private static Vec3 KeepFooting(IWorld world, Agent self, Vec3 move)
    {
        if (move.IsZero) return move;
        var direction = move.Normalized2D();
        foreach (var turn in FootingTurns)
        {
            var heading = turn == 0f ? direction : Rotate(direction, turn);
            if (world.KeepsFooting(self.Position, self.Position + heading * FootingAhead)) return heading * move.Length2D;
        }
        return Vec3.Zero;
    }

    /// <summary>Push away from teammates within <see cref="SeparationRadius"/>, stronger the closer they are.</summary>
    private static Vec3 Separation(Agent self, IReadOnlyList<Agent> agents)
    {
        var push = Vec3.Zero;
        foreach (var other in agents)
        {
            if (other.Slot == self.Slot || other.Role != self.Role) continue;
            if (MathF.Abs(other.Position.Z - self.Position.Z) > ArriveHeight) continue;
            var away = self.Position - other.Position;
            var distance = away.Length2D;
            if (distance >= SeparationRadius) continue;
            // Two bots on the same spot: split them by slot.
            var direction = distance < 1f ? new Vec3(MathF.Cos(self.Slot), MathF.Sin(self.Slot), 0) : away.Normalized2D();
            push += direction * (1f - distance / SeparationRadius);
        }
        return push;
    }

    private static Vec3 Rotate(Vec3 v, float degrees)
    {
        var (sin, cos) = MathF.SinCos(degrees * (MathF.PI / 180f));
        return new Vec3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, 0);
    }
}
