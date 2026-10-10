# ZeBots

> [!WARNING]
> This is a vibe coded slop project. It was written largely by an AI, and nobody has carefully
> reviewed it. Expect bugs, odd behavior and rough edges, and don't run it anywhere you can't
> afford for it to break.

Zombie Escape bot AI for the test servers, as a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
plugin that drives bots through [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller).

- [For server owners](#for-server-owners): what it does, requirements, installing, updating, convars.
- [For developers](#for-developers): layout, building, testing, releasing.

# For server owners

## What it does

- **Zombie bots** hunt the human nearest on foot. They run straight at a human only when a trace
  says the line between them is clear and has floor; otherwise they take the routes players have
  walked, so they go around walls instead of into them. They knife inside 80 units.
- **Human bots** follow the nearest real player along those routes and back away from zombies that
  get close. ZeBots aims for them: they shoot zombies they have a line to, preferring the ones
  players are aiming or firing at, and otherwise look where the shooting is. When a real human
  pings, they go to the ping and hold it for `zb_ping_seconds`. They buy their own primary (and a
  grenade) with CS2Fixes' `c_<weapon>` commands, like players, and are switched back to it when
  the stock AI pulls out the knife.
- Routes come from a walk graph, so no nav mesh is needed. The plugin maps the level itself: from
  every known node it probes a 48-unit grid around it with player-sized traces (steps, ramps,
  drops, crouch-only gaps, jumps, crouch-jumps, ladders) and floods outward over everything
  reachable, a few milliseconds a tick (`zb_explore_ms`), nearest to the players first. Floor in
  enabled hurt and teleport triggers is left out. Everyone's footsteps (bots' too) add to the same
  graph, with how they got there: walking, a jump, crouching, a ladder, a teleporter. Routes players
  walked are preferred over ones only the traces found. The graph is kept for the whole map.
- On a route a bot jumps where the edge needs it (already running), tucks for a crouch-jump,
  crouches through low passages and climbs ladders. After landing short of a jump five times it
  replays how a real player made that jump (recorded when they did). A stuck bot jumps and
  strafes, then routes around the spot for a few seconds. At a map teleporter it walks on into the
  trigger and lets the map move it. Teleporting bots along the route is off unless
  `zb_catchup_seconds` is set.
- Bots move like players. Each tick a bot is accelerated the way a held key would push it, up to
  its weapon's run speed (slower crouched), so knockback and pushes wear off as they do on players.
  The stock AI's own movement keys are cleared, so it cannot add speed on top. On a ladder the bot
  is moved up or down at ladder speed. During freeze time bots stand still. CS2-Bot-Controller's
  input hook does not install on the current CS2 build, so the view is set by teleporting the
  pawn's angles (and the stock AI's look is pointed the same way), and crouch and attack are
  pressed through the stock bot's own input (its button flags and crouch state, and the movement
  duck override).
- Each bot gets its own personality: pace, drift off the straight line, follow distance, how early
  it backs off, which routes and targets it prefers. Teammates keep apart, like the separation rule
  in boids. Zombies avoid areas other zombies are already routed through, so a horde spreads over
  every way to the humans.
- Backing off, making room, drifting or getting unstuck never walks a bot off a ledge: a few traces
  check the floor ahead, and the bot goes along the edge or stands instead. It only goes down
  after the player it follows (or the human it chases), or where its route goes down.
- A bot with nothing to follow is handed back to the stock CS2 bot AI. That AI can't move
  without a nav mesh, so on ZE maps such a bot mostly stands still.

## Requirements

Required:

- [Metamod:Source](https://www.sourcemm.net/) 2.x for CS2 (CounterStrikeSharp loads through it).
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) v1.0.376 or newer (the
  version ZeBots is built against; it runs on .NET 10).
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) v0.7.1: both the Metamod
  plugin and the `BotController-CSS-API` archive (the CounterStrikeSharp half, which provides the
  shared `BotControllerApi.dll`). Without it ZeBots loads, logs a warning and leaves every bot to
  the stock AI.
- A Zombie Escape setup where humans are on CT and zombies on T, and the T team infects (for
  example ZombieReloaded). ZeBots only reads the team and the knife; it does not infect anyone.

Optional:

- [CS2Fixes](https://github.com/Source2ZE/CS2Fixes): human bots buy their primary and grenade with
  its `c_<weapon>` commands (`zb_human_weapon`, `zb_human_grenade`). Without it, those commands do
  nothing and ZeBots gives the weapon directly after two tries (primaries only; no grenade). Every
  other feature works the same.

## Installing

Download `ZeBots.tar.gz` from the [latest release](https://github.com/InterStella0/Ze-Bot/releases/latest),
check it against `ZeBots.tar.gz.sha256`, and extract it over the server's `game/csgo/` directory.
The archive contains only `addons/counterstrikesharp/plugins/ZeBots/`; Metamod, CounterStrikeSharp
and CS2-Bot-Controller must already be installed.

```sh
base=https://github.com/InterStella0/Ze-Bot/releases/latest/download
curl -fLO "$base/ZeBots.tar.gz" -O "$base/ZeBots.tar.gz.sha256"
sha256sum --check ZeBots.tar.gz.sha256
tar -xzf ZeBots.tar.gz -C /path/to/game/csgo
```

Then restart the server, or run `css_plugins load ZeBots` from the server console. On load the
plugin registers its convars (below), so `zb_enable` showing up means it is running. If
CS2-Bot-Controller is missing you will see a warning in the server log and the bots stay on the
stock AI.

To pin a version instead of taking the latest, use
`https://github.com/InterStella0/Ze-Bot/releases/download/v<version>/ZeBots.tar.gz`.

## Updating

Extract the new archive over the old one the same way and restart the server (or
`css_plugins reload ZeBots`). Convar values live in your own `server.cfg`, so nothing is reset.
The walk graph is held in memory only and starts empty on every map, so a reload loses nothing
that a map change wouldn't.

## Convars

These are registered by the plugin when it loads, so set them from `server.cfg` or RCON.

| Convar | Default | Meaning |
|---|---|---|
| `zb_enable` | `1` | `1`: ZeBots drives bots. `0`: stock bot AI. |
| `zb_follow_min` | `200` | A following human bot stops this close to its leader. |
| `zb_follow_max` | `400` | A waiting human bot starts following again past this distance. |
| `zb_catchup_seconds` | `0` | Off by default: bots only walk. Above 0, a bot stuck this many seconds is teleported along the route, and a bot reaching a teleporter on the route is teleported across. |
| `zb_human_weapon` | `p90,bizon,mp7,ump45,mp9,mac10,negev,m249` | Primaries human bots buy with CS2Fixes' `c_<name>` when they have none; each bot picks one. If buying fails twice the weapon is given directly. Empty: none. |
| `zb_human_grenade` | `he` | Grenade human bots buy once a life with `c_<name>`. Empty: none. |
| `zb_drive` | `velocity` | How bots are moved. `velocity`: ZeBots accelerates the bot like a held key and sets its view and buttons; the stock AI keeps running underneath with its movement keys cleared. `usercmd`: the stock AI is frozen and ZeBots writes the bot's input through CS2-Bot-Controller. |
| `zb_trace` | `1` | Use engine traces to check lines to targets, link routes and map the level. `0`, or traces that turn out not to work: bots rely on the engine's spotted state and only on routes walked. |
| `zb_explore_ms` | `3` | Milliseconds per tick spent mapping the level with traces. `0`: learn routes from footsteps only. |
| `zb_ping_seconds` | `20` | Human bots go to a real human's ping and hold it this long. `0`: pings are ignored. |
| `zb_debug` | `0` | Works from your own game console (`zb_debug 1`), where the lines are printed; from the server console or RCON they go to the server log. Once a second, show the graph size and mapping progress, recorded jumps, whether traces work, the speed calibration, freeze time, the active ping and each controlled bot's state (next route edge, failed hops and replays, climb, crouch, aim target, attack, weapon, move type). |

Aim speed and accuracy follow `bot_difficulty`. While `bot_stop` is set, ZeBots releases every bot.

Every teleport is logged with its reason (the route jumps, or the bot was stuck). If
CS2-Bot-Controller refuses a lock or an input, that is logged once.

# For developers

## Layout

| Path | What it is |
|---|---|
| `src/ZeBots.Logic` | The behavior: walk graph, movement, target choice, following, personalities, stuck handling, aim. No game types. |
| `src/ZeBots` | The CounterStrikeSharp plugin. It reads players each pass, runs the logic, and applies it. |
| `tests/ZeBots.Logic.Tests` | xUnit tests for the logic. |
| `fetch-deps.sh` | Downloads `BotControllerApi.dll` (pinned version and sha256) into `deps/`. |
| `build.sh` | Fetches deps, tests, publishes, and packs `dist/ZeBots.tar.gz`. |
| `Directory.Build.props` | Settings shared by every project, including the `<Version>`. |

The logic is kept free of game types behind `IWorld` so it can be unit tested without a server;
the plugin (`ZeBotsPlugin.cs`, `TraceWorld.cs`) is the only part that touches CounterStrikeSharp.
Put new behavior in `ZeBots.Logic` with a test where you can.

## Building

You need the .NET 10 SDK, curl and unzip.

```sh
./build.sh
```

This fetches `BotControllerApi.dll` into `deps/`, runs the tests, publishes the plugin and packs
`dist/ZeBots.tar.gz` in the same layout as a release. `deps/`, `dist/`, `bin/` and `obj/` are
git-ignored. To work on one piece, `dotnet test tests/ZeBots.Logic.Tests` and
`dotnet build src/ZeBots` work after `./fetch-deps.sh` has run once.

Warnings are errors (`TreatWarningsAsErrors`), so a build that passes locally passes in CI.

`BotControllerApi.dll` is only needed to compile. The packed plugin leaves out both it and
`CounterStrikeSharp.API`, since the server provides them and a second copy would load as a
different assembly. To move to a new CS2-Bot-Controller release, change `version` and `sha256` in
`fetch-deps.sh` and update the version in [Requirements](#requirements).

## Testing

```sh
dotnet test tests/ZeBots.Logic.Tests
```

The tests cover the logic only. Anything that needs a running server (traces, bot input, the
controller) has to be checked in game, with `zb_debug 1` to watch what each bot is doing.

## CI

`.github/workflows/ci.yml` runs `build.sh` on every push to `main` and every PR, and uploads
`ZeBots.tar.gz` as a workflow artifact.

## Releasing

The version is `<Version>` in `Directory.Build.props`; the plugin reports it as its module version.
Versions stay `0.0.x` while the project is new.

1. Set the new `<Version>` in `Directory.Build.props`, commit and merge to `main`.
2. Tag it and push the tag:

   ```sh
   git tag v0.0.2 && git push origin v0.0.2
   ```

`.github/workflows/release.yml` checks that the tag matches `<Version>`, builds, and publishes a
GitHub release with `ZeBots.tar.gz` and `ZeBots.tar.gz.sha256` and generated notes. The asset names
stay fixed so `.../releases/latest/download/ZeBots.tar.gz` keeps working.

## Using a release from cs2ze-test

The cs2ze-test worker copies a `ZeBots.tar.gz` into each session's bundle and its `install-mods.sh`
extracts it into the game container. Replace the `zebots` build stage in cs2ze-test's `Dockerfile`
with a pinned download:

```dockerfile
FROM base AS zebots
ARG ZEBOTS_VERSION=0.0.1
ARG ZEBOTS_SHA256=<contents of the release's ZeBots.tar.gz.sha256>
ADD --checksum=sha256:${ZEBOTS_SHA256} \
    https://github.com/InterStella0/Ze-Bot/releases/download/v${ZEBOTS_VERSION}/ZeBots.tar.gz \
    /src/dist/ZeBots.tar.gz
```

The `COPY --from=zebots /src/dist/ZeBots.tar.gz ...` line in the worker stage stays as it is. To
update the server: bump the two args, `git pull`, `docker compose up -d --build`.

## License

MIT, see [LICENSE](LICENSE).
