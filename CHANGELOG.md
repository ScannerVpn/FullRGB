# Changelog

All notable changes to FullRGB are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow semver.

## [Unreleased] — audit fixes: documented guarantees vs. real code

### Fixed — crash

- **The Hardware tab no longer kills the app.** `HidBridge` declared the P/Invoke
  `HidD_GetCaps`, a function that exists in **no** Windows DLL (the real HID parser routine is
  `HidP_GetCaps`, which also returns an NTSTATUS rather than a BOOL). It compiled cleanly and threw
  `EntryPointNotFoundException` on the first device enumeration, so clicking **Hardware** terminated
  the process. Two further bugs in the same struct: `HIDP_CAPS` had `Usage`/`UsagePage` swapped,
  and its reserved block was 13 ushorts short — the native routine fills a fixed 64 bytes, so the
  short declaration would have written past the managed buffer. All three are fixed, and a build
  gate now resolves every `[DllImport]` entry point and asserts the 64-byte struct size.
- Page builds can no longer take the app down: an exception while building the Hardware page is
  logged and shown as an inline error instead of reaching the WPF dispatcher.

### Security

- **Share codes can no longer be a zip bomb.** `Import share code` decompressed with no ceiling,
  so a ~6 KB pasted code could expand to gigabytes and hang the app. Inflation is now capped at
  4 MB (a real profile is a few KB) and the base64 text is length-checked before decoding.
- **Constant-time secret comparison.** The HTTP API token and the companion PIN were compared with
  `==`; both now use `CryptographicOperations.FixedTimeEquals`.
- **Connection cap on the HTTP listener.** Every accepted socket used to spawn an unbounded task,
  so a peer on the same LAN could hold connections open and starve the pool (slowloris-style).
  Concurrent connections are now capped at 16, with a short wait before a socket is dropped.
- **Rate limiting on every route**, not just `/api/auth` (40 req/s). The dispatcher runs on the UI
  thread, so an unthrottled flood previously froze the window, not just the server.
- **Path-traversal guard in the protocol store.** `CommunityStore.Remove` built a path directly
  from a string; names are now validated (no separators, no `..`, no rooted path).
- Companion page now states plainly that its traffic is **plain HTTP with no TLS** — the PIN and
  token are readable by anyone on the same network (en + fa).

### Fixed

- **Overnight schedule rules matched the wrong night.** `Sa 22:00-07:00` fired during Saturday's
  small hours (which is really Friday night) and `Fr 22:00-07:00` lost its Saturday-morning half.
  An overnight range is now anchored to the night it *started* on: on the morning half the weekday
  is checked against yesterday. Documented in the Settings hint (en + fa).
- **Test paint now asks twice, as documented.** The community-protocol safety model, `hid.writeTip`
  and `hid.explain` all promised "two dialogs, one requiring typing" per paint, but only one dialog
  was shown. The second is now a typed confirmation of the device's `VID:PID`.
- **HID collection fallback is no longer silent.** When no collection matches the protocol's
  `usagePage`/`usage`, the fallback is logged (probe) and gets a stronger warning dialog (write),
  because a composite device can route the report to the wrong collection.
- **`GameEventState` is genuinely lock-free.** Its comment promised volatile/Interlocked access
  while every member was a plain auto-property; the state now uses `Volatile` and `Interlocked`
  bit-pattern access (which also removes torn double reads on 32-bit). Public API unchanged.
- **`/api/status` is validated before it goes on the wire** — a change to the dispatcher's internal
  text format now surfaces as a clean JSON error object instead of breaking every client's parser.
- **Update rollback is no longer deleted immediately.** `FullRGB.exe.old` used to be removed on the
  very next start, so a build that crashed on launch had no way back. A marker now counts crash-free
  starts and the rollback copy survives until two clean sessions have been reached.

## [1.6.0] — 2026-09-23 — Round 21: automation, safe mode, and the community layer

### Added — technical / architecture

- **Local control bus (CLI + Named Pipe + HTTP API).** One command set
  (`Automation/CommandDispatcher`), three transports:
  - CLI one-shots: `FullRGB.exe --set-profile gaming`, `--set-effect gamepulse`,
    `--set-color #FF0044`, `--set-brightness 0.6`, `--power off`, `--blackout`,
    `--game-event hp 0.5`, `--rescan`, `--status`, `--list-profiles`, `--help`.
    A running instance is driven over the pipe; with no instance the command is applied at startup.
  - Named pipe `\\.\pipe\fullrgb-ctrl` — one line per command, one line reply; ideal for
    AutoHotkey and PowerShell.
  - HTTP API on `http://127.0.0.1:9372` (token auth; token in `%APPDATA%\FullRGB\api-token`).
    Same-origin companion page; `/api/health` is unauthenticated; `/api/auth` exchanges the
    Settings PIN for the token with brute-force throttling.
- **Engine safe mode / rollback.** `Setup/EngineSafeMode` counts full engine replacements;
  three within ten minutes trip safe mode: the lights park on a dim static colour, a banner
  explains it, and a backoff retry loop (30 s → 5 min) rebuilds the session until it holds,
  then restores the user's profile. Any healthy connect/watchdog reading exits safe mode.
- **Auto-update from GitHub Releases.** Daily check (user-initiated too), download with
  progress, SHA-256 + size + MZ verification BEFORE the swap is recorded, and an atomic
  self-replace at next start (rename running exe → `.old`, move the new exe in, relaunch).
  Owner/repo and asset name are compile-time constants; HTTPS only.
- **Export diagnostics.** One click in Hardware → Engine (and headless
  `--export-diagnostics=path.zip`): system facts, support matrix (VID:PID + reason),
  controllers, engine log tail, the new in-app rolling log (`Diag/AppLog`), and settings.json.
- **Rolling app log** (`Diag/AppLog`) — the engine always had logs; the app's own decisions
  (recoveries, control-bus commands, update steps) now leave a trail too.

### Added — features

- **GamePulse effect (#18)** — reacts to game events pushed over the control bus
  (`game-event hp 0.42 | hit | death | heal | levelup`): health sinks the body colour from
  healthy to danger, hits flash white, deaths hold a red pulse. Styles: solid body,
  health bar, dotted bar. Works with any tool that can POST — no per-game SDK needed.
- **Time-of-day scheduler** — `22:00-07:00=Night`, optional weekday prefixes
  (`Mo-Fr 08:00-18:00=Work`), overnight ranges supported; first matching rule wins and the
  pre-rule profile returns when no rule matches.
- **Per-app profiles, better UX** — "Add current app" button appends the focused exe mapped
  to the active profile (the existing foreground watcher does the switching).
- **Profile import/export** — single-profile JSON file + compact share code
  (`FRGB1-…`, gzip + base64url) you can paste into a chat. Device-specific leftovers are
  pruned automatically on the importing machine.
- **Mobile companion** — Settings → Mobile companion enables the same HTTP server on the LAN;
  a phone browser gets a responsive dark page (PIN → token), with profiles, power, brightness,
  speed, effect + colour, and live status. English/Persian, RTL-aware.

### Added — community / documentation

- **Community HID protocol system** (experimental) — importable JSON definitions for
  driverless OEM mice/keyboards (`schema fullrgb.hid/1`): read-only probes always allowed;
  writes triple-gated (file declares `experimentalWrite` + global switch + per-click double
  confirmation requiring typed text), report sizes bounded 8–64 bytes. Includes
  `Hid/HidBridge` (minimal Win32 HID surface) and examples in `docs/examples/`.
- **CONTRIBUTING.md**, **CODE_OF_CONDUCT.md**, **COMPATIBILITY.md** (searchable device
  list with a submission format), a docs site under `docs/` (deployed via GitHub Pages) with
  troubleshooting for SMBus/elevation and detection questions, screenshots section in the
  README, and `.github/ISSUE_TEMPLATE/device-report.yml` for hardware reports.

### Changed

- Effect count in the UI header is now derived from the catalog (was hardcoded).
- `AppSettings` gained: `AutoUpdateEnabled`, `LastUpdateCheckUtc`, `ControlApiEnabled`,
  `ControlApiPort`, `CompanionEnabled`, `TimeScheduleEnabled`, `TimeScheduleRules`,
  `HidExperimentalWrite`.
- Version gate tests: `--rendertest` grew sections 46–51 (schedule rules, profile share,
  game events, dispatcher, HID validation, updater compare).

### Notes for maintainers

- Releases are built by `windows-build.yml`; the updater only trusts an asset literally named
  `FullRGB.exe` on the latest release of `ScannerVpn/FullRGB`.
- The companion/API server is a raw `TcpListener` (no `HttpListener` ACL prompt); it binds
  loopback unless the companion is enabled, and always requires the session token on `/api/*`.
