---
layout: default
title: Scheduling & per-app profiles
---

# Scheduling & per-app profiles

## Time-of-day schedule (new in 1.6)

`Settings → Time schedule` — enable it and write one rule per line:

```
22:00-07:00=Night
Mo-Fr 08:00-17:00=Work
Sa,Su 12:00-14:00=Movie
```

- Format: `[Days ]HH:MM-HH:MM=ProfileName`
- Days: `Mo Tu We Th Fr Sa Su` — a range (`Mo-Fr`) or a list (`Sa,Su`). Blank = every day.
- **Overnight ranges wrap midnight** (`22:00-07:00` matches 23:30 AND 03:00).
- **The first matching rule wins** — put specific rules on top.
- Equal start/end means "the whole day".
- The profile that was active before a rule took over comes back automatically when no rule
  matches any more.

The rotation scheduler (every N minutes) keeps working independently; whatever both match,
the time rule wins at its tick.

## Per-app profiles

`Settings → Per-app profiles` — map exe names to profiles:

```
cyberpunk2077.exe=Gaming
obs64.exe=Streaming
```

The foreground window is checked every 2 s. The "Add current app" button appends the currently
focused exe mapped to the **active** profile, which makes the whole flow mouse-only: switch to
the profile you want, focus the game, click, done. When the mapped app loses focus, your
previous profile returns.

## GamePulse (game events)

The GamePulse effect (effect #18) reacts to **events** rather than the screen:

| Event | Meaning |
|---|---|
| `hp 0.42` / `hp 42` | health, 0..1 or 0..100 |
| `hit` (optionally `hit 0.8` = strength) | damage flash (white, decays ~450 ms) |
| `death` | red pulse hold for ~1.5 s |
| `heal`, `respawn` | calm green flash |
| `levelup`, `goal`, `explode` | strong accent flash |

Send events from anywhere — CLI (`FullRGB.exe --game-event hp 0.5`), the pipe, or HTTP:

```powershell
Invoke-RestMethod "http://127.0.0.1:9372/api/event?token=$tok" -Method Post `
  -Body '{"event":"hp","value":0.42}' -ContentType 'application/json'
```

Sources that work well: game mods and overlays with HTTP/webhook output, AutoHotkey scripts
that watch the screen, stream bots. The effect's editor has the colours (danger / healthy),
the style (solid body, health bar, dotted bar) and a test panel (Hit / Death / Heal + an HP
slider) so you can try it without wiring anything.
