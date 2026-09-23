---
layout: default
title: Automation — CLI, Named Pipe, HTTP API
---

# Automation

Everything below drives a **running** FullRGB through one shared command set. The CLI, the
named pipe and the HTTP API accept the same commands, so a script written against one works
on the others.

The control bus is on by default (`Settings` page stores the switch and port). For security:

- The HTTP API listens on `127.0.0.1` only, and **every `/api/*` call needs the session
  token** — a web page in your browser cannot drive your LEDs.
- The token is regenerated at every start and written to `%APPDATA%\FullRGB\api-token`
  (same-user readable only).
- The **mobile companion** (see the bottom of this page) additionally binds the same port to
  the LAN; it stays token-gated.

## CLI one-shots

```powershell
FullRGB.exe --set-profile gaming
FullRGB.exe --set-effect gamepulse
FullRGB.exe --set-color #FF0044 --secondary      # secondary colour of the effect
FullRGB.exe --set-brightness 0.6                 # 0..1 (0..100 also accepted)
FullRGB.exe --set-speed 0.8
FullRGB.exe --power off                          # or on
FullRGB.exe --blackout                           # one black frame, effects stay running
FullRGB.exe --game-event hp 0.42                 # feed the GamePulse effect
FullRGB.exe --game-event hit
FullRGB.exe --rescan
FullRGB.exe --status                             # one-line JSON summary
FullRGB.exe --list-profiles
FullRGB.exe --help
```

Behaviour: if an instance is already running, the command is forwarded over the named pipe and
the process exits (exit code 0 = OK, 1 = ERR, 2 = bad usage). If **no** instance is running,
the command is applied as soon as the new instance's engine session comes up — so a startup
script can set the profile before the window exists. `--status` / `--list-profiles` answer
from disk in that case.

## Named pipe `\\.\pipe\fullrgb-ctrl`

One connection per command: send a line, read one line back (`OK` or `ERR …`, or the payload
for `status` / `profiles`). No token needed — the pipe lives in your own session.

PowerShell:

```powershell
function Send-FullRGB([string]$cmd) {
  $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'fullrgb-ctrl', [System.IO.Pipes.PipeDirection]::InOut)
  $pipe.Connect(2000)
  $writer = [System.IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
  $writer.WriteLine($cmd)
  $reader = [System.IO.StreamReader]::new($pipe)
  $reply = $reader.ReadLine()
  $pipe.Dispose(); $reply
}
Send-FullRGB "set-profile gaming"
Send-FullRGB "status"
```

AutoHotkey (v2) can use [`NamedPipeWrapper`](https://www.autohotkey.com/boards/viewtopic.php?t=87276)
or plain `FileOpen("\\.\pipe\fullrgb-ctrl", "w")`-style access the same way.

## Command reference (pipe + HTTP share it)

| Command | Argument | Effect |
|---|---|---|
| `set-profile` | profile name | switch profile |
| `profiles` | — | list profile names (one per line) |
| `set-effect` | type name | set the global effect: `solid`, `gradient`, `rainbow`, `cycle`, `breathing`, `wave`, `comet`, `blink`, `fire`, `temp`, `audio`, `custom`, `spectrum`, `scanner`, `sparkle`, `plasma`, `ambient`, `gaming`, `gamepulse` |
| `set-color` | `#RRGGBB` | primary colour |
| `set-color2` | `#RRGGBB` | secondary colour |
| `set-brightness` | `0..1` (or `0..100`) | global brightness |
| `set-speed` | `0..1` | global speed |
| `power` | `on` \| `off` | start/stop the effect loops |
| `blackout` | — | paint everything black once |
| `game-event` | `name [value]` | `hp`, `hit`, `damage`, `heal`, `death`, `respawn`, `levelup`, `goal`, `explode` |
| `rescan` | — | re-detect devices |
| `status` | — | JSON session summary |
| `version` | — | app version |
| `help` | — | command list |

## HTTP API (`http://127.0.0.1:9372` by default)

All non-`auth` `/api/*` routes need the token: append `?token=…` or send the
`X-FullRGB-Token` header. Replies are JSON with `"ok":true/false`.

| Route | Method | Body / query |
|---|---|---|
| `/api/health` | GET | — (no token; bare liveness) |
| `/api/auth` | POST | `{"pin":"123456"}` → `{"ok":true,"token":"…"}` (5 wrong tries per minute max) |
| `/api/status` | GET | — |
| `/api/profiles` | GET | — |
| `/api/profile` | POST | `{"name":"gaming"}` |
| `/api/effect` | POST | `{"type":"gamepulse"}` |
| `/api/color` | POST | `{"hex":"#FF0044","secondary":false}` |
| `/api/brightness` | POST | `{"value":0.6}` |
| `/api/speed` | POST | `{"value":0.8}` |
| `/api/power` | POST | `{"on":true}` |
| `/api/blackout` | POST | — |
| `/api/event` | POST | `{"event":"hp","value":0.42}` |
| `/api/rescan` | POST | — |

Examples:

```powershell
$tok = Get-Content "$env:APPDATA\FullRGB\api-token"
Invoke-RestMethod "http://127.0.0.1:9372/api/status?token=$tok"
Invoke-RestMethod "http://127.0.0.1:9372/api/profile?token=$tok" -Method Post -Body '{"name":"gaming"}' -ContentType 'application/json'
Invoke-RestMethod "http://127.0.0.1:9372/api/event?token=$tok"  -Method Post -Body '{"event":"hit"}'     -ContentType 'application/json'
```

```bash
TOK=$(cat "$APPDATA/FullRGB/api-token")
curl -s "http://127.0.0.1:9372/api/status?token=$TOK"
```

### Game events for GamePulse

Any tool that can POST can drive the lights: a game mod, a Lua overlay, an AHK script reading
the screen, a Twitch bot. Send `hp` (0..1 or 0..100), `hit` (0..1 strength), `death`, `heal`,
`levelup`… then select the **GamePulse** effect in the app. The body colour sinks from the
healthy colour toward the danger colour as health drops, hits flash white, deaths hold a red
pulse. Style (solid body / health bar / dotted bar) and both colours are in the effect editor.

## Mobile companion

`Settings → Mobile companion → Allow control from other devices on this network` re-binds the
HTTP server to the LAN. The card shows the URL(s) and the 6-digit PIN (regenerated every
start). On the phone: open the URL, type the PIN once — the token is kept in the browser.
You get profiles, power, blackout, brightness/speed, effect + colour, and a game-event tester.
Everything stays on your network; nothing is forwarded anywhere.

## Headless diagnostics

`FullRGB.exe --export-diagnostics[=path.zip]` builds the same bug-report zip the Hardware
page button builds, without the GUI — useful in support scripts and CI.
