# Development

## Layout

| Path | What it is |
|---|---|
| `src/ZeBots.Logic` | The behavior: walk graph, movement, targeting, personalities, stuck handling, aim. No game types. |
| `src/ZeBots` | The CounterStrikeSharp plugin. Reads players each pass, runs the logic, applies the result. |
| `tests/ZeBots.Logic.Tests` | xUnit tests for the logic. |
| `fetch-deps.sh` | Downloads `BotControllerApi.dll` (pinned version and sha256) into `deps/`. |
| `build.sh` | Fetches deps, tests, publishes, and packs `dist/ZeBots.tar.gz`. |

The logic has no game types, so it is unit tested without a server. Only the plugin touches
CounterStrikeSharp.

## How bots get around

**Walk graph** (`WalkGraph.cs`, `Explorer.cs`). Nodes are floor positions; edges say how to get
between them: walk, jump, crouch, ladder or teleporter. Every player's footsteps add a node about
every 48 units. The `Explorer` also probes outward from known nodes with traces to find floor
nobody has walked. Those edges cost 1.5x, so walked routes win. The graph is cleared on map start.

**Routing.** Dijkstra over the graph (`WalkGraph.Search`). An edge costs its length, plus extra for
a jump, a crouch (x2.5) or a ladder (x1.5). A bot picks the target with the cheapest route (a
zombie picks a human, a human bot picks the nearest real player), keeps it unless another is
clearly cheaper, and replans every 0.4-0.8 s. If no known route reaches the target, it goes as
close as the routes do and waits. A zombie with a clear line to a human close enough runs straight
at them instead.

**Variety** (`Persona.cs`). Each bot rolls its own speed, drift off the line, follow and retreat
distances, and a seeded cost noise per edge, so bots choose different routes. Zombies also claim
the areas along their route (`Crowd.cs`), and the next zombie to plan pays extra to enter those, so a
horde spreads over the ways to the humans.

**Steering** (`BotBrain.Steer`). Route direction, plus drift, plus separation from teammates, then
a check that the move doesn't walk off a ledge.

### Boids?

Only the separation rule. Teammates of the same role within 72 units push each other apart,
harder the closer they are. There is no cohesion or alignment, and routes come from graph search,
not flocking. The horde spreading out is `Crowd`'s route penalty, not boids.

### Where human bots look

In order: the zombie they are shooting; else the average direction the humans who are *firing*
are aiming (real players weigh more than bots); else the next route waypoint; else the direction
their leader faces. So a bot follows where you aim while you or others shoot, but walking along
it looks where it walks. Turn speed, jitter and how close the aim must be before firing come from
`bot_difficulty` (`Settings.cs`).

### Pings

A ping from a living, non-bot human on CT (`EventPlayerPing`) replaces any earlier one. For
`zb_ping_seconds`, human bots route to it (the nearest node within 128 units, or as close as the
routes go), stop within 64 units and walk again past 128. Backing away from a close zombie still
takes priority. After that they go back to following a player.

## Building and testing

You need the .NET 10 SDK, curl and unzip.

```sh
./build.sh                                  # fetch deps, test, pack dist/ZeBots.tar.gz
dotnet test tests/ZeBots.Logic.Tests        # tests only
```
