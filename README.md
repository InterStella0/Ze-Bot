# ZeBots

> [!WARNING]
> This is a vibe coded slop project. It was written largely by an AI, and nobody has carefully
> reviewed it. Expect bugs, odd behavior and rough edges, and don't run it anywhere you can't
> afford for it to break.

Zombie Escape bot AI for CS2, as a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
plugin that drives bots through [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller).

## What it does

- **Zombie bots** hunt the nearest human on foot. They run straight at a human when the line is
  clear, otherwise they follow routes players have walked, and knife inside 80 units.
- **Human bots** follow the nearest real player, back away from zombies, and shoot the ones they
  can see, preferring those players are aiming at. They go to a player's ping and hold it, and buy
  their own weapon and grenade.
- **No nav mesh needed.** The plugin maps the level itself with traces, and learns from everyone's
  footsteps. Bots jump, crouch, climb ladders and use teleporters where the route needs it.
- **Bots move like players**, so knockback and pushes wear off as they normally would.
- **Each bot has its own personality**, and teammates keep apart so a horde spreads out.
- **Bots never walk off a ledge** unless following a player down or their route goes down.
- A bot with nothing to follow is handed back to the stock bot AI, which mostly stands still on
  ZE maps.

## Requirements

- [Metamod:Source](https://www.sourcemm.net/) 2.x for CS2.
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) v1.0.376 or newer.
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) v0.7.1: the Metamod plugin
  and the `BotController-CSS-API` archive. Without it ZeBots logs a warning and leaves bots to the
  stock AI.
- A Zombie Escape setup with humans on CT, zombies on T, and T infecting (e.g. ZombieReloaded).
  ZeBots does not infect anyone.
- Optional: [CS2Fixes](https://github.com/Source2ZE/CS2Fixes), so human bots buy weapons with its
  `c_<weapon>` commands. Without it they are given a primary directly (no grenade).

## Installing

Extract the release over the server's `game/csgo/` directory. It only adds
`addons/counterstrikesharp/plugins/ZeBots/`.

```sh
base=https://github.com/InterStella0/Ze-Bot/releases/latest/download
curl -fLO "$base/ZeBots.tar.gz" -O "$base/ZeBots.tar.gz.sha256"
sha256sum --check ZeBots.tar.gz.sha256
tar -xzf ZeBots.tar.gz -C /path/to/game/csgo
```

Then restart the server.

## Updating

Extract the new release over the old one and restart the server.

## Convars

Set them from `server.cfg` or RCON.

| Convar | Default | Meaning |
|---|---|---|
| `zb_enable` | `1` | `0` hands every bot back to the stock AI. |
| `zb_follow_min` | `200` | A following human bot stops this close to its leader. |
| `zb_follow_max` | `400` | A waiting human bot follows again past this distance. |
| `zb_catchup_seconds` | `0` | Above 0, a bot stuck this long is teleported along its route. `0`: bots only walk. |
| `zb_human_weapon` | `p90,bizon,mp7,ump45,mp9,mac10,negev,m249` | Primaries human bots buy, one per bot. Empty: none. |
| `zb_human_grenade` | `he` | Grenade human bots buy once a life. Empty: none. |
| `zb_drive` | `velocity` | How bots are moved. Use `usercmd` only if `velocity` misbehaves. |
| `zb_trace` | `1` | Use engine traces for sight lines and mapping. `0` relies on walked routes only. |
| `zb_explore_ms` | `3` | Milliseconds per tick spent mapping the level. `0`: learn from footsteps only. |
| `zb_ping_seconds` | `20` | How long human bots hold a player's ping. `0`: ignore pings. |
| `zb_debug` | `0` | `1` prints each bot's state once a second (to your console, or the server log from RCON). |

Aim follows `bot_difficulty`. While `bot_stop` is set, ZeBots releases every bot.

---

Building from source: see [DEVELOPMENT.md](DEVELOPMENT.md). MIT license.
