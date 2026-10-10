using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZeBots.Logic;

namespace ZeBots;

/// <summary>
/// Zombie Escape bot AI. Every other tick it reads the living players, adds their footsteps to the
/// map's <see cref="WalkGraph"/>, and runs <see cref="BotBrain"/> for each bot. Every tick the
/// <see cref="Explorer"/> also spends a few milliseconds of traces mapping floor no one has walked.
/// How the result reaches the bot depends on zb_drive:
///
/// <list type="bullet">
/// <item><c>velocity</c> (default): every tick ZeBots accelerates the bot the way a held movement
/// key would (or moves it along a ladder, or replays how a player took a jump), sets its view, and
/// presses attack and crouch through the stock bot's own input (its button flags and crouch state),
/// which it sends to the game as its usercmd. The stock AI keeps running underneath, but its own
/// movement keys are cleared every tick and its look is pointed where ZeBots aims.</item>
/// <item><c>usercmd</c>: the stock AI is frozen through CS2-Bot-Controller (Lock All), the view is
/// set by teleporting the pawn's angles, and movement and buttons are written into the usercmd.</item>
/// </list>
///
/// Bots ZeBots has no goal for (no humans left, no real player to follow) are left to the stock AI,
/// as are all bots while zb_enable is 0 or bot_stop is set. Without a nav
/// mesh, which ZE maps do not have, that AI does not walk anywhere. During freeze time bots are
/// held still, as players are.
/// </summary>
public sealed class ZeBotsPlugin : BasePlugin
{
    public override string ModuleName => "ZeBots";
    // From <Version> in Directory.Build.props, so the release tag, archive and plugin agree.
    public override string ModuleVersion { get; } =
        typeof(ZeBotsPlugin).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";
    public override string ModuleAuthor => "InterStella0";
    public override string ModuleDescription => "Zombie Escape bot AI: zombies hunt humans, human bots follow real players and shoot zombies";

    private static readonly PluginCapability<IBotControllerApi> BotControllerCapability = new("botcontroller:api");

    /// <summary>One logic pass every this many ticks (32 passes a second on a 64-tick server).</summary>
    private const int TicksPerPass = 2;
    private const float CvarRefreshSeconds = 1f;
    private const float WeaponCheckSeconds = 2f;
    /// <summary>Held buttons are re-injected every pass and last a little longer than one.</summary>
    private const int HoldButtonsMs = 80;
    /// <summary>Teleport destinations are graph nodes (feet positions); start just above the floor.</summary>
    private const float TeleportLift = 8f;
    /// <summary>Running speed with a knife, when the weapon's own is unknown.</summary>
    private const float DefaultRunSpeed = 250f;
    /// <summary>Jump take-off speed (sv_jump_impulse).</summary>
    private const float JumpSpeed = 301.993f;
    /// <summary>
    /// The drive calibrates how far the game actually moves a bot per unit of velocity, from bots
    /// moving at least this fast on the ground, and adjusts the speed it asks for to match.
    /// </summary>
    private const float CalibrateAbove = 150f;
    private const float CalibrationRate = 0.02f;
    private const float WarnRatio = 1.25f;
    /// <summary>Traces are checked against this many grounded players per pass.</summary>
    private const int TraceChecksPerPass = 2;
    /// <summary>Route searches per pass (each bot replans about twice a second), and how long they may take.</summary>
    private const int PlansPerPass = 6;
    private const double PlanBudgetMs = 4;
    /// <summary>Hurt and teleport triggers are re-read this often (maps switch them during a round).</summary>
    private const float HazardRefreshSeconds = 2f;
    /// <summary>Crouched players move at this fraction of their run speed.</summary>
    private const float DuckSpeed = 0.34f;
    /// <summary>Tries at buying with CS2Fixes' command before giving the weapon directly.</summary>
    private const int BuyAttempts = 2;
    /// <summary>A shot this recent counts a player as firing (where bots look, and what they shoot).</summary>
    private const float FiringSeconds = 0.5f;
    /// <summary>
    /// A human bot off its primary (the stock AI pulls the knife out to run) is switched back after
    /// this long; one holding a grenade gets longer to throw it. Tries are this far apart.
    /// </summary>
    private const float OffPrimarySeconds = 0.3f;
    private const float GrenadeSeconds = 3f;
    private const float SwitchInterval = 0.5f;
    /// <summary>The stock AI's own movement keys, cleared every tick: ZeBots moves the bot.</summary>
    private const ulong StockMoveButtons = (ulong)(PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Moveleft | PlayerButtons.Moveright | PlayerButtons.Jump | PlayerButtons.Speed | PlayerButtons.Walk);

    // Registered as console commands by CounterStrikeSharp (public FakeConVar fields), so the
    // server.cfg and RCON can set them.
    public FakeConVar<bool> Enable = new("zb_enable", "ZE bot AI: 1 = ZeBots drives bots, 0 = stock bot AI", true);
    public FakeConVar<float> FollowMin = new("zb_follow_min", "A following human bot stops about this close to its leader", 200f);
    public FakeConVar<float> FollowMax = new("zb_follow_max", "A waiting human bot follows again past about this distance", 400f);
    public FakeConVar<float> CatchupSeconds = new("zb_catchup_seconds", "Teleport a bot stuck this long along its route, and across teleporters (0 = never teleport)", 0f);
    public FakeConVar<string> HumanWeapon = new("zb_human_weapon", "Primaries human bots buy with CS2Fixes c_ commands, comma separated; each bot picks one (empty = none)", "p90,bizon,mp7,ump45,mp9,mac10,negev,m249");
    public FakeConVar<string> HumanGrenade = new("zb_human_grenade", "Grenade human bots buy once a life with a CS2Fixes c_ command (empty = none)", "he");
    public FakeConVar<string> Drive = new("zb_drive", "velocity = ZeBots sets the bot's velocity, view and buttons; usercmd = Bot-Controller input", "velocity");
    public FakeConVar<bool> UseTraces = new("zb_trace", "Use engine traces to see targets, link routes and map the level (0 = engine spotted state and walked routes only)", true);
    public FakeConVar<float> ExploreMs = new("zb_explore_ms", "Milliseconds of traces per tick spent mapping floor no one has walked (0 = learn routes from footsteps only)", 3f);
    public FakeConVar<float> PingSeconds = new("zb_ping_seconds", "Human bots go to a human player's ping and hold it this many seconds (0 = ignore pings)", 20f);

    private static readonly HashSet<string> PrimaryWeapons =
    [
        "weapon_mp9", "weapon_mac10", "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_p90", "weapon_bizon",
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_famas", "weapon_galilar", "weapon_aug",
        "weapon_sg556", "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1", "weapon_nova",
        "weapon_xm1014", "weapon_mag7", "weapon_sawedoff", "weapon_m249", "weapon_negev",
    ];

    private sealed class DrivenBot(Persona persona)
    {
        public BotState Brain { get; } = new(persona);
        public bool Locked { get; set; }
        public long MovementId { get; set; }
        public LockTarget WeaponLock { get; set; } = LockTarget.None;
        public bool HasPrimary { get; set; }
        public float WeaponCheckedAt { get; set; } = float.NegativeInfinity;
        public int BuyAttempts { get; set; }
        public bool WeaponGiven { get; set; }
        public bool GrenadeBought { get; set; }
        /// <summary>Velocity mode: the pawn and where it goes, applied every tick until the next pass.</summary>
        public CCSPlayerPawn? Pawn { get; set; }
        public Vec3 Move { get; set; }
        public float RunSpeed { get; set; } = DefaultRunSpeed;
        public bool JumpPending { get; set; }
        /// <summary>On a ladder: vertical speed; <see cref="Move"/> is then the exact sideways move.</summary>
        public float Climb { get; set; }
        /// <summary>Held through the stock bot's input every tick.</summary>
        public bool Duck { get; set; }
        public bool Attack { get; set; }
        /// <summary>The buttons (and crouch) ZeBots set last tick, to take back when no longer wanted.</summary>
        public ulong ButtonsSet { get; set; }
        public bool DuckSet { get; set; }
        /// <summary>Where the bot was last tick, for the speed calibration.</summary>
        public Vec3? LastOrigin { get; set; }
        public bool WasOnGround { get; set; }
        public bool Drove { get; set; }
        /// <summary>Last commanded yaw, to show in zb_debug whether the view actually follows it.</summary>
        public float CommandedYaw { get; set; }
        /// <summary>Velocity mode: the view to hold every tick until the next pass.</summary>
        public bool Aim { get; set; }
        public float Pitch { get; set; }
        /// <summary>The velocity ZeBots set last tick, to tell what else has moved the bot since.</summary>
        public Vec3? LastSet { get; set; }
        /// <summary>Replaying how a player took the edge the bot is on.</summary>
        public MimicRun? Mimic { get; set; }
        /// <summary>The weapon in hand: a gun (fire), and whether it fires while held.</summary>
        public bool HoldingGun { get; set; }
        public bool FullAuto { get; set; } = true;
        public float OffPrimarySince { get; set; } = float.NaN;
        public float SwitchedAt { get; set; } = float.NegativeInfinity;
        public int SwitchTries { get; set; }
    }

    private readonly WalkGraph _graph;
    private readonly Explorer _explorer;
    private readonly Crowd _crowd = new();
    private readonly TraceWorld _traces = new();
    private readonly System.Diagnostics.Stopwatch _exploreClock = new();
    private float _hazardsReadAt = float.NegativeInfinity;
    private int _exploredSinceDebug;
    private readonly Dictionary<int, Role> _roles = new();
    private readonly Dictionary<int, DrivenBot> _bots = new();
    private readonly HashSet<string> _warned = new();
    private readonly Random _random = new();
    private readonly Vector _velocity = new(0, 0, 0);
    private readonly QAngle _angles = new(0, 0, 0);
    private IBotControllerApi? _api;
    private int _tick;
    private float _cvarsReadAt = float.NegativeInfinity;
    private float _debugAt = float.NegativeInfinity;
    /// <summary>zb_debug from the server console or RCON: the bot state goes to the server log.</summary>
    private bool _debugLog;
    /// <summary>zb_debug from a player's console: the bot state goes to that player's console.</summary>
    private readonly HashSet<int> _debugWatchers = new();
    private int _difficulty = 1;
    private bool _botStop;
    private MoveParams _move = new();
    /// <summary>How far the game moves a bot per unit of velocity; 1 unless something doubles it up.</summary>
    private float _speedRatio = 1f;
    private readonly Dictionary<int, float> _firedAt = new();
    private Ping? _ping;
    private CCSGameRulesProxy? _rules;
    private bool _frozen;

    public ZeBotsPlugin()
    {
        _graph = new WalkGraph();
        _explorer = new Explorer(_graph);
    }

    private bool UsercmdDrive => string.Equals(Drive.Value.Trim(), "usercmd", StringComparison.OrdinalIgnoreCase);

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerPing>(OnPing);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        // A command, not a FakeConVar: those ignore anything typed in a player's console.
        AddCommand("zb_debug", "Show each controlled bot's state once a second (1 = on, 0 = off): in your console, or the server log from the server console or RCON", OnDebugCommand);
    }

    private void OnDebugCommand(CCSPlayerController? player, CommandInfo command)
    {
        var watcher = player is { IsValid: true, IsBot: false } ? player.Slot : (int?)null;
        if (player is not null && watcher is null) return;
        if (command.ArgCount >= 2)
        {
            switch (command.GetArg(1).Trim().ToLowerInvariant())
            {
                case "1" or "true" or "on":
                    if (watcher is { } on) _debugWatchers.Add(on);
                    else _debugLog = true;
                    break;
                case "0" or "false" or "off":
                    if (watcher is { } off) _debugWatchers.Remove(off);
                    else _debugLog = false;
                    break;
                default:
                    command.ReplyToCommand("Usage: zb_debug 1 (on) or zb_debug 0 (off)");
                    return;
            }
        }
        var enabled = watcher is { } slot ? _debugWatchers.Contains(slot) : _debugLog;
        command.ReplyToCommand(watcher is null
            ? $"zb_debug = {(enabled ? 1 : 0)} (bot state in the server log)"
            : $"zb_debug = {(enabled ? 1 : 0)} (bot state in your console once a second)");
    }

    private HookResult OnDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { } player) _debugWatchers.Remove(player.Slot);
        return HookResult.Continue;
    }

    /// <summary>A zb_debug line, to the server log and the consoles of players who asked for it.</summary>
    private void DebugLine(string line)
    {
        if (_debugLog) Logger.LogInformation("{Line}", line);
        foreach (var slot in _debugWatchers.ToList())
        {
            var watcher = Utilities.GetPlayerFromSlot(slot);
            if (watcher is { IsValid: true, IsBot: false }) watcher.PrintToConsole(line);
            else _debugWatchers.Remove(slot);
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _api = BotControllerCapability.Get();
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "Could not get the CS2-Bot-Controller API");
        }
        if (_api is null) Logger.LogWarning("CS2-Bot-Controller is not loaded; ZeBots leaves every bot to the stock AI");
        else Logger.LogInformation("ZeBots is driving bots (CS2-Bot-Controller ABI {Abi})", _api.AbiVersion);
    }

    public override void Unload(bool hotReload) => ReleaseAll();

    private void OnMapStart(string map)
    {
        // A new map: what was learned about the last one's routes is no use.
        _graph.Clear();
        _explorer.Clear();
        _hazardsReadAt = float.NegativeInfinity;
        _crowd.Clear();
        _roles.Clear();
        _firedAt.Clear();
        _ping = null;
        _rules = null;
        // The old map's pawns are gone: do not touch them on the way out.
        foreach (var bot in _bots.Values) bot.Pawn = null;
        ReleaseAll();
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Everyone respawns. The routes stay known; where each player was does not carry over.
        _graph.ForgetWalkers();
        _crowd.Clear();
        _ping = null;
        ReleaseAll();
        return HookResult.Continue;
    }

    /// <summary>A real human's ping: human bots go there (see <see cref="BotBrain"/>).</summary>
    private HookResult OnPing(EventPlayerPing @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is { IsValid: true, IsBot: false } && player.Team == CsTeam.CounterTerrorist)
            _ping = new Ping(player.Slot, new Vec3(@event.X, @event.Y, @event.Z), Server.CurrentTime);
        return HookResult.Continue;
    }

    /// <summary>Who is shooting: human bots look where the shooting is and shoot what players shoot at.</summary>
    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is { IsValid: true } && Classify(@event.Weapon) == WeaponKind.Gun) _firedAt[player.Slot] = Server.CurrentTime;
        return HookResult.Continue;
    }

    private void OnTick()
    {
        if (_api is null) return;
        if (++_tick % TicksPerPass == 0) Pass();
        if (!UsercmdDrive)
        {
            var dt = Server.TickInterval > 0f ? Server.TickInterval : 1f / 64f;
            var now = Server.CurrentTime;
            foreach (var bot in _bots.Values)
            {
                Apply(bot, dt, now);
                ApplyButtons(bot);
            }
        }
        Explore();
    }

    /// <summary>Maps floor no one has walked yet, for as long as zb_explore_ms allows this tick.</summary>
    private void Explore()
    {
        var budget = ExploreMs.Value;
        if (budget <= 0f || !Enable.Value || World() is not { } world) return;
        _exploreClock.Restart();
        while (_exploreClock.Elapsed.TotalMilliseconds < budget && _explorer.Step(world)) _exploredSinceDebug++;
    }

    private void Pass()
    {
        var now = Server.CurrentTime;
        if (now - _cvarsReadAt >= CvarRefreshSeconds)
        {
            _cvarsReadAt = now;
            _difficulty = ReadInt("bot_difficulty", 1);
            _botStop = ReadInt("bot_stop", 0) != 0;
            _move = new MoveParams
            {
                Accelerate = ReadFloat("sv_accelerate", 5.5f),
                AirAccelerate = ReadFloat("sv_airaccelerate", 12f),
                Friction = ReadFloat("sv_friction", 5.2f),
                StopSpeed = ReadFloat("sv_stopspeed", 80f),
                AirMaxWishSpeed = ReadFloat("sv_air_max_wishspeed", 30f),
            };
        }

        _frozen = FreezePeriod();
        var world = World();
        if (world is not null && now - _hazardsReadAt >= HazardRefreshSeconds)
        {
            _hazardsReadAt = now;
            _traces.RefreshHazards();
        }
        var agents = new List<Agent>();
        var players = new Dictionary<int, (CCSPlayerController Controller, CCSPlayerPawn Pawn)>();
        var traceChecks = 0;
        foreach (var controller in Utilities.GetPlayers())
        {
            if (!controller.IsValid || controller.IsHLTV) continue;
            Role? role = controller.Team switch
            {
                CsTeam.CounterTerrorist => Role.Human,
                CsTeam.Terrorist => Role.Zombie,
                _ => null,
            };
            var pawn = controller.PlayerPawn.Value;
            if (role is null || pawn is null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var origin = pawn.AbsOrigin;
            if (origin is null) continue;

            var mask = pawn.EntitySpottedState.SpottedByMask;
            var spottedBy = (mask.Length > 0 ? mask[0] : 0u) | (mask.Length > 1 ? (ulong)mask[1] << 32 : 0UL);
            var angles = pawn.EyeAngles;
            var flags = pawn.Flags;
            var velocity = pawn.AbsVelocity;
            var agent = new Agent(controller.Slot, controller.IsBot, role.Value, new Vec3(origin.X, origin.Y, origin.Z),
                (flags & (uint)PlayerFlags.FL_ONGROUND) != 0, angles.Y, angles.X, spottedBy,
                Ducked: (flags & (uint)PlayerFlags.FL_DUCKING) != 0, OnLadder: pawn.MoveType == MoveType_t.MOVETYPE_LADDER,
                Velocity: velocity is null ? Vec3.Zero : new Vec3(velocity.X, velocity.Y, velocity.Z),
                Firing: _firedAt.TryGetValue(controller.Slot, out var fired) && now - fired <= FiringSeconds);
            agents.Add(agent);
            players[agent.Slot] = (controller, pawn);

            // Infection (or a respawn on the other team) can move a player across the map: that is
            // not a route anyone can walk.
            if (_roles.TryGetValue(agent.Slot, out var was) && was != agent.Role) _graph.Forget(agent.Slot);
            _roles[agent.Slot] = agent.Role;
            // How real players take jumps is kept, for bots that cannot make them their own way.
            _graph.Record(agent.Slot, agent.Position, agent.OnGround, world, agent.Ducked, agent.OnLadder, now, agent.Velocity, real: !agent.IsBot);
            // Map around wherever players are first: that is where bots will need routes.
            _explorer.Seed(_graph.NodeOf(agent.Slot));
            if (world is not null && agent.OnGround && traceChecks++ < TraceChecksPerPass) _traces.Check(agent.Position);
        }
        foreach (var slot in _roles.Keys.Where(slot => !players.ContainsKey(slot)).ToList())
        {
            // Dead (or gone): the next footstep after a respawn starts a new walk.
            _roles.Remove(slot);
            _graph.Forget(slot);
            _firedAt.Remove(slot);
        }

        var settings = new BrainSettings
        {
            FollowMin = Math.Max(0f, FollowMin.Value),
            FollowMax = Math.Max(FollowMax.Value, FollowMin.Value),
            CatchupSeconds = Math.Max(0f, CatchupSeconds.Value),
            PingSeconds = Math.Max(0f, PingSeconds.Value),
            Aim = AimSkill.ForDifficulty(_difficulty),
        };
        var nav = new Navigation(_graph, world, _crowd, PlansPerPass, PlanBudgetMs) { Ping = _ping };
        var active = Enable.Value && !_botStop;
        var usercmd = UsercmdDrive;
        var alive = new HashSet<int>();
        foreach (var agent in agents)
        {
            if (!agent.IsBot || !active) continue;
            alive.Add(agent.Slot);
            if (!_bots.TryGetValue(agent.Slot, out var bot)) _bots[agent.Slot] = bot = new DrivenBot(Persona.Roll(_random));
            if (_frozen)
            {
                var (frozenController, frozenPawn) = players[agent.Slot];
                Hold(agent, frozenController, frozenPawn, bot, now);
                continue;
            }
            var command = BotBrain.Think(bot.Brain, agent, agents, nav, now, settings, _random);
            if (!command.Controlled)
            {
                Release(agent.Slot, bot);
                continue;
            }
            var (controller, pawn) = players[agent.Slot];
            if (command.TeleportTo is { } to)
            {
                Logger.LogInformation("[ZeBots] {Name} ({Role}) teleported, {Reason}: ({FromX:0} {FromY:0} {FromZ:0}) -> ({X:0} {Y:0} {Z:0})",
                    controller.PlayerName, agent.Role, command.Reason == TeleportReason.Gap ? "the route crosses a teleporter" : $"stuck {CatchupSeconds.Value:0}s",
                    agent.Position.X, agent.Position.Y, agent.Position.Z, to.X, to.Y, to.Z);
            }
            if (usercmd) DriveUsercmd(agent, controller, pawn, bot, command, now);
            else DriveVelocity(agent, controller, pawn, bot, command, now);
        }
        foreach (var slot in _bots.Keys.Where(slot => !alive.Contains(slot)).ToList())
        {
            Release(slot, _bots[slot]);
            _bots.Remove(slot);
        }

        if ((_debugLog || _debugWatchers.Count > 0) && now - _debugAt >= 1f)
        {
            _debugAt = now;
            var traces = world is null ? _traces.Failure ?? "off" : "on";
            var pinged = _ping is { } ping ? $"slot {ping.Slot} {now - ping.At:0}s ago" : "none";
            DebugLine($"[ZeBots] graph {_graph.Count} nodes ({_explorer.Explored} mapped, {_explorer.Pending} to map, +{_exploredSinceDebug}/s), {_graph.Recordings} recorded jumps, traces {traces}, {_traces.Hazards} hurt/teleport triggers avoided, speed ratio {_speedRatio:0.00}, frozen {_frozen}, ping {pinged}, {_bots.Count} bots driven");
            _exploredSinceDebug = 0;
            foreach (var (slot, bot) in _bots)
            {
                if (!players.TryGetValue(slot, out var player)) continue;
                var velocity = player.Pawn.AbsVelocity;
                var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
                var brain = bot.Brain;
                DebugLine(
                    $"[ZeBots] {player.Controller.PlayerName} slot {slot} {brain.Role} drive={(usercmd ? "usercmd" : "velocity")} target={brain.TargetSlot} direct={brain.Direct} " +
                    $"route={brain.PathIndex}/{brain.Path.Count} next={NextEdge(brain)} hops={brain.HopFailures} mimic={brain.Mimic is not null}/{brain.MimicFailures} following={brain.Following} stuck={brain.Stuck.StuckFor:0.0}s " +
                    $"speed={speed:0}/{bot.RunSpeed:0} climb={bot.Climb:0} duck={bot.Duck}/{DuckAmount(player.Pawn):0.00} aim={brain.AimSlot} attack={bot.Attack} gun={bot.HoldingGun} " +
                    $"weapon={ActiveWeapon(player.Pawn)?.DesignerName ?? "-"} move={player.Pawn.MoveType} yaw={player.Pawn.EyeAngles.Y:0}/{bot.CommandedYaw:0}");
            }
        }
    }

    /// <summary>What the bot's next route edge takes (walk, jump, crouch, ladder, teleport), for zb_debug.</summary>
    private string NextEdge(BotState brain)
    {
        var path = brain.Path;
        var index = brain.PathIndex;
        if (index <= 0 || index >= path.Count || !_graph.TryGetEdge(path[index - 1], path[index], out var edge)) return "-";
        return edge.Kind == EdgeKind.Walk ? edge.Moves == Moves.None ? "walk" : edge.Moves.ToString().Replace(", ", "+").ToLowerInvariant() : edge.Kind.ToString().ToLowerInvariant();
    }

    private static float DuckAmount(CCSPlayerPawn pawn)
    {
        var movement = pawn.MovementServices;
        return movement is null || movement.Handle == IntPtr.Zero ? 0f : new CCSPlayer_MovementServices(movement.Handle).DuckAmount;
    }

    /// <summary>The traces, unless they are switched off or have been found not to work.</summary>
    private TraceWorld? World()
    {
        if (!UseTraces.Value) return null;
        if (!_traces.Broken) return _traces;
        WarnOnce("traces", $"Engine traces do not work ({_traces.Failure}); bots fall back to the engine's spotted state and walked routes only");
        return null;
    }

    /// <summary>Whether the round's freeze time is on: players cannot move yet, and nor do bots.</summary>
    private bool FreezePeriod()
    {
        try
        {
            if (_rules is not { IsValid: true }) _rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            return _rules?.GameRules?.FreezePeriod ?? false;
        }
        catch (Exception error)
        {
            WarnOnce("rules", $"Could not read the game rules ({error.Message}); bots may move during freeze time");
            return false;
        }
    }

    /// <summary>
    /// Freeze time: the bot stands where it spawned (the velocity drive would otherwise move it while
    /// players cannot), keeps its weapons sorted, and does not count the wait as being stuck.
    /// </summary>
    private void Hold(Agent agent, CCSPlayerController controller, CCSPlayerPawn pawn, DrivenBot bot, float now)
    {
        bot.Brain.Stuck.Reset();
        CancelMovement(agent.Slot, bot);
        EnsureWeapon(agent, controller, pawn, bot, now);
        bot.Pawn = UsercmdDrive ? null : pawn;
        bot.Move = Vec3.Zero;
        bot.Climb = 0f;
        bot.JumpPending = false;
        bot.Duck = false;
        bot.Attack = false;
        bot.Aim = false;
        bot.Mimic = null;
        bot.LastSet = null;
    }

    /// <summary>
    /// Velocity mode: ZeBots supplies where to go, where to look and when to attack; it is applied
    /// every tick by <see cref="Apply"/> and <see cref="ApplyButtons"/>.
    /// </summary>
    private void DriveVelocity(Agent agent, CCSPlayerController controller, CCSPlayerPawn pawn, DrivenBot bot, BotCommand command, float now)
    {
        var api = _api!;
        if (bot.Locked)
        {
            api.Unlock(agent.Slot, LockKind.All);
            bot.Locked = false;
        }
        CancelMovement(agent.Slot, bot);
        EnsureWeapon(agent, controller, pawn, bot, now);

        bot.Pawn = pawn;
        bot.Move = command.Move;
        bot.Climb = command.Climb;
        bot.Duck = command.Duck;
        bot.Mimic = command.Mimic;
        // A human only fires a gun: with the knife (or a grenade) out, it waits to be switched back.
        bot.Attack = command.Attack && (agent.Role == Role.Zombie || bot.HoldingGun);
        bot.JumpPending |= command.Jump && command.Mimic is null;
        bot.Aim = true;
        bot.CommandedYaw = command.Yaw;
        bot.Pitch = command.Pitch;
        if (command.TeleportTo is { } to)
        {
            pawn.Teleport(new Vector(to.X, to.Y, to.Z + TeleportLift), (QAngle?)null, new Vector(0, 0, 0));
            bot.LastOrigin = null;
            bot.LastSet = null;
        }
    }

    /// <summary>
    /// Velocity mode, every tick: holds crouch and attack through the stock bot's own input, which
    /// the game turns into the bot's usercmd: its button flags and crouch state, and the movement
    /// duck override as well, in case the bot's input does not carry it. The stock AI's own
    /// movement keys are taken out, so it cannot walk the bot on top of ZeBots' drive. A gun that
    /// does not fire while held (a pistol) is clicked: attack every other tick.
    /// </summary>
    private void ApplyButtons(DrivenBot bot)
    {
        var pawn = bot.Pawn;
        if (pawn is null || !pawn.IsValid) return;
        var attack = bot.Attack && (bot.FullAuto || _tick % 2 == 0);
        var wanted = (bot.Duck ? (ulong)PlayerButtons.Duck : 0UL) | (attack ? (ulong)PlayerButtons.Attack : 0UL);
        try
        {
            SetButtons(pawn, bot.ButtonsSet | StockMoveButtons, wanted, bot.Duck, bot.DuckSet || bot.Duck);
            bot.ButtonsSet = wanted;
            bot.DuckSet = bot.Duck;
        }
        catch (Exception error)
        {
            WarnOnce("buttons", $"Could not press buttons for a bot ({error.Message}); bots cannot crouch or attack on purpose");
        }
    }

    /// <summary>
    /// Replaces the buttons ZeBots held (<paramref name="held"/>) with <paramref name="wanted"/>,
    /// with the stock AI's movement keys and move speeds cleared, and sets crouch when
    /// <paramref name="setDuck"/>.
    /// </summary>
    private static void SetButtons(CCSPlayerPawn pawn, ulong held, ulong wanted, bool duck, bool setDuck)
    {
        var stockBot = pawn.Bot;
        if (stockBot is not null && stockBot.Handle != IntPtr.Zero)
        {
            stockBot.ButtonFlags = (stockBot.ButtonFlags & ~held) | wanted;
            stockBot.ForwardSpeed = 0f;
            stockBot.LeftSpeed = 0f;
            if (setDuck) stockBot.IsCrouching = duck;
        }
        if (!setDuck) return;
        var movement = pawn.MovementServices;
        if (movement is not null && movement.Handle != IntPtr.Zero) new CCSPlayer_MovementServices(movement.Handle).DuckOverride = duck;
    }

    /// <summary>
    /// Velocity mode, every tick: accelerates the bot toward where it is going as a held movement
    /// key would (<see cref="Movement.Accelerate"/>), so it gets up to its weapon's run speed and no
    /// faster, and knockback and pushes play out as they do on players. On a ladder it is moved
    /// along it; replaying a player, it moves as they did. The view is set too, and the stock AI's
    /// look pointed the same way so it does not turn the bot back.
    /// </summary>
    private void Apply(DrivenBot bot, float dt, float now)
    {
        var pawn = bot.Pawn;
        if (pawn is null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
        var origin = pawn.AbsOrigin;
        var current = pawn.AbsVelocity;
        if (origin is null || current is null) return;
        var position = new Vec3(origin.X, origin.Y, origin.Z);
        var velocity = new Vec3(current.X, current.Y, current.Z);
        var onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;

        // How far the game actually moved the bot for the velocity it had. Should be 1; if the game
        // moves a driven bot further than its velocity says, ask for that much less.
        if (bot.LastOrigin is { } last && bot.Drove && bot.WasOnGround && onGround && velocity.Length2D >= CalibrateAbove)
        {
            var ratio = Vec3.Distance2D(last, position) / dt / velocity.Length2D;
            if (ratio is > 0.25f and < 4f)
            {
                _speedRatio += (ratio - _speedRatio) * CalibrationRate;
                if (_speedRatio > WarnRatio) WarnOnce("speed", $"The game moves driven bots {_speedRatio:0.00}x their velocity; ZeBots is slowing them to match");
            }
        }
        var wasOnGround = bot.WasOnGround;
        var lastSet = bot.LastSet;
        bot.LastOrigin = position;
        bot.WasOnGround = onGround;
        bot.Drove = false;
        bot.LastSet = null;

        var next = Next(bot, pawn, position, velocity, onGround, wasOnGround, lastSet, dt, now);
        QAngle? angles = null;
        if (bot.Aim)
        {
            _angles.X = bot.Pitch;
            _angles.Y = bot.CommandedYaw;
            _angles.Z = 0f;
            angles = _angles;
            Look(pawn, bot);
        }
        if (next == velocity)
        {
            // Left as it is: still what ZeBots set, as far as taking back the stock AI's keys goes.
            bot.LastSet = velocity;
            bot.Drove = true;
            next = null;
        }
        if (next is null && angles is null) return;
        Vector? push = null;
        if (next is { } set)
        {
            _velocity.X = set.X;
            _velocity.Y = set.Y;
            _velocity.Z = set.Z;
            push = _velocity;
            bot.Drove = true;
            bot.LastSet = set;
        }
        pawn.Teleport((Vector?)null, angles, push);
    }

    /// <summary>The velocity to give the bot this tick, or null when ZeBots is not moving it.</summary>
    private Vec3? Next(DrivenBot bot, CCSPlayerPawn pawn, Vec3 position, Vec3 velocity, bool onGround, bool wasOnGround, Vec3? lastSet, float dt, float now)
    {
        if (bot.Mimic is { } run)
        {
            var drive = run.Drive(now, position, onGround);
            if (!drive.Done)
            {
                bot.Duck = drive.Duck;
                bot.JumpPending = false;
                var fall = onGround ? Movement.Grounded(velocity).Z : velocity.Z;
                return new Vec3(drive.Velocity.X, drive.Velocity.Y, drive.Jump ? JumpSpeed : fall);
            }
            bot.Mimic = null;
            bot.Move = Vec3.Zero;
        }
        if (bot.Move.IsZero && !bot.JumpPending && bot.Climb == 0f) return null;

        Vec3 next;
        if (bot.Climb != 0f)
        {
            // On a ladder: straight up (or down, no faster than climbing) at ladder speed, holding on.
            var side = bot.Move * BotBrain.LadderSideSpeed;
            next = new Vec3(side.X, side.Y, bot.Climb > 0f ? bot.Climb : MathF.Max(velocity.Z, bot.Climb));
        }
        else
        {
            var maxSpeed = bot.RunSpeed / Math.Clamp(_speedRatio, 0.5f, 3f);
            if ((pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0) maxSpeed *= DuckSpeed;
            // Whatever the stock AI's own keys added since last tick does not count.
            if (lastSet is { } set && wasOnGround && onGround) velocity = Movement.Govern(velocity, set, maxSpeed, _move, dt);
            next = Movement.Accelerate(velocity, bot.Move, maxSpeed, onGround, _move, dt);
            if (bot.JumpPending && onGround) next = next with { Z = JumpSpeed };
        }
        bot.JumpPending = false;
        return next;
    }

    /// <summary>Points the stock AI's look where ZeBots aims, at rest, so its own turning does not fight the view.</summary>
    private void Look(CCSPlayerPawn pawn, DrivenBot bot)
    {
        try
        {
            var stockBot = pawn.Bot;
            if (stockBot is null || stockBot.Handle == IntPtr.Zero) return;
            stockBot.LookYaw = bot.CommandedYaw;
            stockBot.LookPitch = bot.Pitch;
            stockBot.LookYawVel = 0f;
            stockBot.LookPitchVel = 0f;
        }
        catch (Exception error)
        {
            WarnOnce("look", $"Could not set a bot's look ({error.Message}); the stock AI may turn bots away from where ZeBots aims");
        }
    }

    private enum WeaponKind
    {
        Gun,
        Knife,
        Grenade,
    }

    private static WeaponKind Classify(string? name)
    {
        if (string.IsNullOrEmpty(name)) return WeaponKind.Knife;
        if (name.Contains("knife", StringComparison.Ordinal) || name.Contains("bayonet", StringComparison.Ordinal)) return WeaponKind.Knife;
        if (name.Contains("grenade", StringComparison.Ordinal) || name.Contains("flashbang", StringComparison.Ordinal)
            || name.Contains("molotov", StringComparison.Ordinal) || name.Contains("decoy", StringComparison.Ordinal)) return WeaponKind.Grenade;
        return WeaponKind.Gun;
    }

    private static CBasePlayerWeapon? ActiveWeapon(CCSPlayerPawn pawn)
    {
        var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
        return weapon is { IsValid: true } ? weapon : null;
    }

    /// <summary>
    /// Reads the weapon in hand: its run speed (slowed while the bot is tagged), whether it is a
    /// gun, and whether it fires while attack is held.
    /// </summary>
    private static void ReadWeapon(CCSPlayerPawn pawn, DrivenBot bot)
    {
        var speed = DefaultRunSpeed;
        bot.HoldingGun = false;
        bot.FullAuto = true;
        try
        {
            if (ActiveWeapon(pawn) is { } weapon)
            {
                bot.HoldingGun = Classify(weapon.DesignerName) == WeaponKind.Gun;
                var data = new CCSWeaponBase(weapon.Handle).VData;
                if (data is not null)
                {
                    var values = data.MaxSpeed.Values;
                    if (values.Length > 0 && values[0] is > 1f and < 1000f) speed = values[0];
                    bot.FullAuto = data.IsFullAuto;
                }
            }
        }
        catch (Exception)
        {
            // Unknown weapon data: run at knife speed, hold attack.
        }
        var modifier = pawn.VelocityModifier;
        if (modifier is > 0f and <= 1f) speed *= modifier;
        bot.RunSpeed = speed;
    }

    /// <summary>Usercmd mode: the stock AI is frozen and ZeBots writes the bot's input.</summary>
    private void DriveUsercmd(Agent agent, CCSPlayerController controller, CCSPlayerPawn pawn, DrivenBot bot, BotCommand command, float now)
    {
        var api = _api!;
        bot.Pawn = null;
        if (!bot.Locked)
        {
            bot.Locked = api.Lock(agent.Slot, LockKind.All);
            if (!bot.Locked) WarnOnce("lock", "CS2-Bot-Controller refused Lock All; the stock AI keeps control of the bot's aim and movement");
        }
        EnsureWeapon(agent, controller, pawn, bot, now);

        var angles = new QAngle(command.Pitch, command.Yaw, 0);
        bot.CommandedYaw = command.Yaw;
        if (command.TeleportTo is { } to) pawn.Teleport(new Vector(to.X, to.Y, to.Z + TeleportLift), angles, new Vector(0, 0, 0));
        else pawn.Teleport((Vector?)null, angles, (Vector?)null);

        if (bot.MovementId > 0 && !api.UpdateUsercmdMovement(agent.Slot, bot.MovementId, command.Forward, command.Left)) bot.MovementId = 0;
        if (bot.MovementId <= 0)
        {
            // Fails (-1) until Bot-Controller's PlayerRunCommand hook is installed; retried next pass.
            bot.MovementId = Math.Max(0, api.StartUsercmdMovement(agent.Slot, command.Forward, command.Left));
            if (bot.MovementId == 0) WarnOnce("movement", "CS2-Bot-Controller refused the movement override (its PlayerRunCommand hook is not active); try zb_drive velocity");
        }

        ulong held = 0;
        if (command.Attack) held |= (ulong)PlayerButtons.Attack;
        if (command.Duck) held |= (ulong)PlayerButtons.Duck;
        if (held != 0) Inject(agent.Slot, held, HoldButtonsMs);
        // Zero duration: pressed for one command, then released, so each jump is a fresh press.
        if (command.Jump) Inject(agent.Slot, (ulong)PlayerButtons.Jump, 0);
    }

    private void Inject(int slot, ulong buttons, int durationMs)
    {
        if (_api!.InjectUsercmd(slot, buttons, durationMs) < 0)
            WarnOnce("inject", "CS2-Bot-Controller refused a button injection (its PlayerRunCommand hook is not active); bots cannot be made to attack or jump");
    }

    /// <summary>
    /// Human bots buy a primary if they have none (ZR humans often spawn with a pistol) and a
    /// grenade, the way players do on this server: CS2Fixes' c_&lt;weapon&gt; commands. If buying
    /// does not work, the weapon is given directly. Humans are held on the primary (and switched
    /// back to it when the stock AI pulls out something else); zombies on the knife, which is what
    /// infects.
    /// </summary>
    private void EnsureWeapon(Agent agent, CCSPlayerController controller, CCSPlayerPawn pawn, DrivenBot bot, float now)
    {
        var api = _api!;
        ReadWeapon(pawn, bot);
        LockTarget wanted;
        if (agent.Role == Role.Zombie)
        {
            wanted = LockTarget.Slot3;
        }
        else
        {
            if (now - bot.WeaponCheckedAt >= WeaponCheckSeconds)
            {
                bot.WeaponCheckedAt = now;
                bot.HasPrimary = HasPrimary(pawn);
                if (!bot.HasPrimary && PickWeapon(bot.Brain.Persona) is { } weapon)
                {
                    if (bot.BuyAttempts < BuyAttempts)
                    {
                        bot.BuyAttempts++;
                        controller.ExecuteClientCommandFromServer($"c_{weapon}");
                    }
                    else if (!bot.WeaponGiven && PrimaryWeapons.Contains($"weapon_{weapon}"))
                    {
                        bot.WeaponGiven = true;
                        WarnOnce("buy", $"Human bots could not buy with c_{weapon} (CS2Fixes weapon commands); giving them weapons directly");
                        controller.GiveNamedItem($"weapon_{weapon}");
                    }
                    // Locked on a later pass, once the new weapon is in its slot.
                    bot.WeaponLock = LockTarget.None;
                    return;
                }
                if (!bot.GrenadeBought && CommandName(HumanGrenade.Value) is { } grenade)
                {
                    bot.GrenadeBought = true;
                    controller.ExecuteClientCommandFromServer($"c_{grenade}");
                }
            }
            if (!bot.HasPrimary) return;
            KeepPrimary(agent, controller, pawn, bot, now);
            wanted = LockTarget.Slot1;
        }
        if (bot.WeaponLock == wanted) return;
        if (api.Lock(agent.Slot, wanted)) bot.WeaponLock = wanted;
        else WarnOnce("weapon", "CS2-Bot-Controller refused a weapon lock; bots may switch weapons on their own");
    }

    /// <summary>
    /// The stock AI pulls the knife out to run (and the pistol when it likes), and then does not
    /// shoot: a human bot with a primary is switched back to it, through CS2-Bot-Controller and, if
    /// the bot is still off it after that, the way a player's "use" command does.
    /// </summary>
    private void KeepPrimary(Agent agent, CCSPlayerController controller, CCSPlayerPawn pawn, DrivenBot bot, float now)
    {
        var active = ActiveWeapon(pawn);
        if (active is not null && PrimaryWeapons.Contains(active.DesignerName))
        {
            bot.OffPrimarySince = float.NaN;
            bot.SwitchTries = 0;
            return;
        }
        if (float.IsNaN(bot.OffPrimarySince)) bot.OffPrimarySince = now;
        var patience = active is not null && Classify(active.DesignerName) == WeaponKind.Grenade ? GrenadeSeconds : OffPrimarySeconds;
        if (now - bot.OffPrimarySince < patience || now - bot.SwitchedAt < SwitchInterval || Primary(pawn) is not { } primary) return;
        bot.SwitchedAt = now;
        bot.SwitchTries++;
        var switched = _api!.SwitchBotWeapon(agent.Slot, primary.AttributeManager.Item.ItemDefinitionIndex);
        if (!switched) WarnOnce("switch", "CS2-Bot-Controller refused to switch a bot's weapon; switching with the use command instead");
        if (!switched || bot.SwitchTries > 2) controller.ExecuteClientCommandFromServer($"use {primary.DesignerName}");
    }

    /// <summary>This bot's pick from zb_human_weapon, the same every time for one persona.</summary>
    private string? PickWeapon(Persona persona)
    {
        var choices = HumanWeapon.Value.Split(',').Select(CommandName).OfType<string>().ToList();
        return choices.Count == 0 ? null : choices[(persona.Seed & int.MaxValue) % choices.Count];
    }

    /// <summary>A CS2Fixes buy command name ("p90", also from "weapon_p90"), or null if empty or odd.</summary>
    private static string? CommandName(string value)
    {
        var name = value.Trim().ToLowerInvariant();
        if (name.StartsWith("weapon_", StringComparison.Ordinal)) name = name["weapon_".Length..];
        return name.Length > 0 && name.All(char.IsAsciiLetterOrDigit) ? name : null;
    }

    private static bool HasPrimary(CCSPlayerPawn pawn) => Primary(pawn) is not null;

    private static CBasePlayerWeapon? Primary(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons is null) return null;
        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon is { IsValid: true } && PrimaryWeapons.Contains(weapon.DesignerName)) return weapon;
        }
        return null;
    }

    private void CancelMovement(int slot, DrivenBot bot)
    {
        if (bot.MovementId <= 0) return;
        _api!.CancelUsercmdMovement(slot, bot.MovementId);
        bot.MovementId = 0;
    }

    private void Release(int slot, DrivenBot bot)
    {
        if (bot.Pawn is { IsValid: true } pawn && (bot.ButtonsSet != 0 || bot.DuckSet))
        {
            try
            {
                SetButtons(pawn, bot.ButtonsSet, 0, false, bot.DuckSet);
            }
            catch (Exception)
            {
                // The pawn is going away anyway.
            }
        }
        bot.ButtonsSet = 0;
        bot.DuckSet = false;
        bot.Pawn = null;
        bot.Move = Vec3.Zero;
        bot.Climb = 0f;
        bot.Aim = false;
        bot.Mimic = null;
        bot.LastSet = null;
        bot.OffPrimarySince = float.NaN;
        bot.SwitchTries = 0;
        bot.LastOrigin = null;
        _crowd.Release(slot);
        if (_api is null) return;
        CancelMovement(slot, bot);
        if (bot.Locked)
        {
            _api.Unlock(slot, LockKind.All);
            bot.Locked = false;
        }
        if (bot.WeaponLock != LockTarget.None)
        {
            _api.Unlock(slot, LockKind.Weapon);
            bot.WeaponLock = LockTarget.None;
        }
    }

    private void ReleaseAll()
    {
        foreach (var (slot, bot) in _bots) Release(slot, bot);
        _bots.Clear();
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) Logger.LogWarning("[ZeBots] {Message}", message);
    }

    private static int ReadInt(string name, int fallback)
    {
        var convar = ConVar.Find(name);
        if (convar is null) return fallback;
        return convar.Type switch
        {
            ConVarType.Bool => convar.GetPrimitiveValue<bool>() ? 1 : 0,
            ConVarType.Int16 => convar.GetPrimitiveValue<short>(),
            ConVarType.Int32 => convar.GetPrimitiveValue<int>(),
            ConVarType.UInt32 => (int)convar.GetPrimitiveValue<uint>(),
            ConVarType.Float32 => (int)convar.GetPrimitiveValue<float>(),
            _ => fallback,
        };
    }

    private static float ReadFloat(string name, float fallback)
    {
        var convar = ConVar.Find(name);
        if (convar is null) return fallback;
        return convar.Type switch
        {
            ConVarType.Float32 => convar.GetPrimitiveValue<float>(),
            ConVarType.Float64 => (float)convar.GetPrimitiveValue<double>(),
            ConVarType.Int32 => convar.GetPrimitiveValue<int>(),
            _ => fallback,
        };
    }
}
