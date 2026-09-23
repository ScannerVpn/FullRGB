# FullRGB — PLAN.md

**Last updated:** 2026-09-23 (round 21: v1.6.0 — the app becomes controllable and self-defending: local automation bus (CLI + named pipe + HTTP + mobile companion), engine safe mode with backoff, GitHub-Releases auto-update with a verified atomic swap, diagnostics zip export, GamePulse game-event effect, time-of-day scheduling, profile share codes, guarded community HID protocol system, full community docs; round 20: the machine watches itself — an independent frame watchdog catches a stalled session after sleep/wake/power-loss and rebuilds it without the user; round 18: a real per-user Inno Setup installer; round 17: v1.5.0 — the device inventory is remembered, so a launch no longer rescans from scratch; round 16: the lighting rebuilds itself after sleep/hibernate; round 15: v1.4.0 — arena UI redesign ported, review defects fixed, Persian-first defaults)
**Repo root:** `G:\Ai\RGB Control` (git since v1.0; pushed to `main` @ `9de3102` = round 20 + missing `DeviceCache.cs`)
**Status:** WORKING on the real rig. Round 21 is code-complete AND compile-verified (2026-09-23: full Release build with the .NET 8 SDK cross-targeting the Windows TFM — **0 errors, 0 warnings**; fixes applied: HID `?? return` → pattern match, raw-string opener, ref-readonly GUID, ambiguous WPF/WinForms names, missing usings, out-param capture, stackalloc-in-async, CS4014 wake-rescan). Still to run on a Windows box/CI: the behaviour gates (`--rendertest` sections 46–51, `--uitest`, `--export-diagnostics`, pipe/HTTP round-trip) — see §Round 21 "verification checklist".

| Gate | Command | Latest result |
|---|---|---|
| Logic | `FullRGB.exe --rendertest` | **ALL RENDER TESTS PASSED** (round 20 Debug build, 230 asserts, +44; round 21 adds sections 46–51: schedule rules, profile share, game events, dispatcher, HID validation, updater compare) |
| UI (XAML/resources/glyphs/l10n/bundle/USB) | `FullRGB.exe --uitest` | **ALL UI TESTS PASSED** (round 20 Debug build; round 21 adds Settings cards + GamePulse chip — expect this gate to re-verify them) |
| Stall recovery (real rig) | `FullRGB.exe --stalltest --seconds=120` | **watchdog acted by itself at 20 s; frames 784→3608; hardware updating again: True** |
| Real hardware | `FullRGB.exe --fxtest --seconds=14` | **devices=4, framesSent=1684, errors=0** (dist29 — not re-run in round 16: the user's GUI was running and holds the SDK session) |
| Engine task | `FullRGB.exe --enginetask=status` | `registered=True matchesThisInstall=True pawnio=True elevated=False` |
| USB inventory | `FullRGB.exe --usbscan` | 9 devices; mouse + keyboard identified by product string |
| Screenshots | `FullRGB.exe --uishot` | 4 PNGs at 1280×900 in `%TEMP%\fullrgb-shots` |

**Latest build: the round-20 Debug tree** (`src\FullRGB\bin\Debug\net8.0-windows\win-x64`) — gates
run there. `dist34` = v1.5.1 (round 19; see §10e below).
**NOT released:** the latest GitHub release is still **v1.3.0** and no `v1.4.x` tag exists on the
remote (verified 2026-09-22 18:35 with `gh release list` + `git ls-remote --tags`).

---

## Round 21 (2026-09-23) — v1.6.0: controllable, self-defending, community-ready

Scope: all fourteen asks from the maintainer — five technical (automation, HID community
protocols, auto-update, engine rollback/safe-mode, exportable diagnostics), five user-facing
(game/media sync, scheduler, per-app profiles polish, profile share, mobile companion), four
docs/community items. Code-complete in this tree; compile-verified 2026-09-23 (Release, 0 errors / 0 warnings) — behaviour gates still need a Windows session or the CI runner.

### New files (all in `src/FullRGB/` unless noted)

| File | What it is |
|---|---|
| `Automation/IControlTarget.cs` | the command surface the bus drives (implemented by MainWindow) |
| `Automation/CommandDispatcher.cs` | ONE parser/executor: `set-profile`, `set-effect`, `set-color[2]`, `set-brightness`, `set-speed`, `power`, `blackout`, `game-event`, `rescan`, `status`, `profiles`, `help`, `version` |
| `Automation/ControlHub.cs` | named pipe `fullrgb-ctrl` (one request per connection) + raw-`TcpListener` HTTP server (no HttpListener ACL prompt); token auth, PIN→token exchange with a 5/min brute-force gate; `SendOneShot` is the CLI's pipe client |
| `Companion/CompanionPage.cs` | embedded single-page mobile UI (en/fa, RTL, dark): PIN modal, profiles, power, brightness/speed, effect+colour, game-event tester |
| `Update/AppUpdater.cs` | GitHub Releases check (24 h cadence) → download with progress → SHA-256+size+MZ verify → `pending.json` → atomic self-swap at next start (rename running exe→`.old`, move new in, relaunch) |
| `Diag/AppLog.cs` | rolling in-app log (600-line ring + `logs\app-YYYYMMDD.log`, 14-day retention) |
| `Diag/DiagnosticsExport.cs` | the bug-report zip (system / controllers / support-matrix / app-log / engine-log / settings / report); also `SupportMatrixText()` shared by UI and `--export-diagnostics` |
| `Hid/CommunityProtocols.cs` | `fullrgb.hid/1` JSON model + strict validation (schema, hex VID/PID, 8–64-byte reports, "must do something") + store under `%APPDATA%\FullRGB\hid\` |
| `Hid/HidBridge.cs` | minimal Win32 HID surface: SetupAPI interface enumeration, attributes/caps, one GET_FEATURE probe, one guarded SET_FEATURE solid paint — nothing else |
| `Setup/EngineSafeMode.cs` | crash-loop bookkeeping: 3 engine replacements in 10 min ⇒ unsafe; backoff 30/60/120/300 s; enter/exit state machine |
| `Config/ScheduleRules.cs` | `Mo-Fr 22:00-07:00=Night` parser/evaluator (overnight ranges wrap midnight; first match wins) |
| `Config/ProfileShare.cs` | single-profile JSON envelope + `FRGB1-…` short code (gzip+base64url) + name dedupe |
| `Sensors/GameEventState.cs` | lock-free game-event state (hp 0..1/0..100, hit envelope, death hold) fed by the bus, read by the renderer |
| `MainWindow.Automation.cs` | IControlTarget implementation, hub lifecycle, time-schedule tick, safe-mode wiring + banner, updater UI, pending-CLI replay |
| `docs/` (repo root) | Jekyll docs site: index, automation, scheduling, hid-protocols, troubleshooting, diagnostics, examples; `docs-site.yml` workflow publishes it |
| `.github/ISSUE_TEMPLATE/` | device-report / bug / feature / question forms |
| `COMPATIBILITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `CHANGELOG.md` | community layer |
| `docs/screenshots/hero-banner.png` | generated banner (placeholder until `--uishot` captures are committed) |

### Changed files

- `Effects/Effects.cs`: `GamePulse = 18` (APPEND-ONLY, serialized as numbers!), game fields on
  `EffectContext`, the GamePulse renderer (danger↔healthy body + hit flash + death hold;
  AudioMode reused for solid/bar/dots styles).
- `Effects/EffectEngine.cs` + `MainWindow.Effects.cs` (preview): fill `GameEventState.Fill(ctx)`.
- `MainWindow.Effects.cs`: GamePulse catalog chip (vector ECG path — no MDL2 dependency) + params.
- `MainWindow.xaml`: safe-mode banner; Settings cards for time schedule / companion / updates /
  profile share buttons; "Add current app".
- `MainWindow.xaml.cs`: hub + schedule + updater start in `Loaded`; `OnSessionHealthy()` +
  `ApplyPendingCommand()` in `OnConnected`; `NoteEngineReplaced()` on the watchdog's engine
  verdict; safe-mode exits on any healthy reading; effects.count derived from the catalog.
- `MainWindow.Settings.cs`: profile share handlers, `FgAddCurrent_Click`, LoadProfileToUi
  covers the new cards.
- `MainWindow.Hardware.cs`: Export diagnostics button; the whole Community protocols card
  (import/probe/test-paint/write toggle with typed double confirmation).
- `App.xaml.cs`: `--help/--version`, automation verbs (`TryBuildAutomationCommand` → pipe
  one-shot, or parked in `App.PendingCommandVerb` for the no-instance case), `--export-diagnostics`,
  update swap before the single-instance mutex.
- `Config/ProfileStore.cs`: new settings (`AutoUpdateEnabled`, `LastUpdateCheckUtc`,
  `ControlApiEnabled`, `ControlApiPort`, `CompanionEnabled`, `TimeScheduleEnabled`,
  `TimeScheduleRules`, `HidExperimentalWrite`) + normalization.
- `L10n.cs`: ~90 new keys in BOTH en and fa (MissingKeys gate would fail otherwise).
- `FullRGB.csproj`: version 1.6.0.

### Design decisions worth remembering

- **One command set, three transports.** The CLI does not implement its own logic; it builds a
  dispatcher command. Same for HTTP routes. Testing the dispatcher (rendertest §49) covers all.
- **TcpListener, not HttpListener** for the HTTP API: non-localhost prefixes need an ACL
  grant (netsh) which would break the "no admin" promise; a 200-line raw parser with a 64 KB
  body cap is the trade.
- **Token, even on loopback.** A browser-resident attacker could otherwise POST to
  `127.0.0.1:9372` (CSRF/DNS-rebinding). `/api/health` is the only unauthenticated route
  besides the page and the PIN exchange; `/api/auth` is throttled 5 tries/min.
- **The updater verifies BEFORE recording `pending.json`** (size floor 5 MB, `MZ` header,
  SHA-256) and re-verifies the hash at swap time; owner/repo and asset name `FullRGB.exe` are
  compile-time constants. The swap is a rename (legal on a running exe) + move + relaunch.
- **Safe mode parks on a STATIC dim accent colour** — never an animation, which would look
  like "still working". The user's profile name is captured and restored by `Exit()`.
- **HID writes are triple-gated by design** (file declares + global typed switch + per-paint
  typed confirm), and the payload shape is constrained to `id + prefix + N×RGB + zeros` —
  no arbitrary bytes. The store re-validates every file on every load.
- **GamePulse is event-driven, not screen-driven.** The screen already has Ambient/Gaming;
  events need no per-game SDK and work from any HTTP-capable tool.

### Verification checklist for the next Windows session (step 1 done via cross-compile)

1. ~~`dotnet build -c Debug`~~ — **DONE 2026-09-23, better than planned**: full Release
   cross-compile with the real .NET 8 SDK (`-p:EnableWindowsTargeting`) → 0 errors, 0 warnings
   after fixing 4 syntax roots (HID `?? return`, RenderTests raw string + shadowed `back`,
   HID ref-readonly GUID) and 13 semantic ones (ambiguous WPF/WinForms `Clipboard`/`Application`,
   missing usings, properties as `out`, private-set from sibling class, out-param captured by a
   local function, `stackalloc` in async, CS4014 on the wake-rescan). The name-collision worry
   with `MainWindow.Automation.cs` did not materialise.
2. `bash tools/verify.sh Debug` — rendertest now has sections 46–51; uitest re-checks the new
   XAML names (`TimeSchedChk`, `CompanionChk`, `UpdateChk`, `SafeModeBanner`, …) and the new
   l10n keys (both languages). The CI workflow runs this gate on every push.
3. `FullRGB.exe --help`, `--version`, `--status` (no instance), `--export-diagnostics=test.zip`
   — the diagnostics verb is now ALSO a CI gate (`windows-build.yml`, Headless diagnostics gate).
4. With the GUI running: pipe test (`Send-FullRGB "set-profile gaming"` from docs), HTTP
   status/profile/event round-trip with the token file, companion page on a phone.
5. `--game-event hit` against a rig with the GamePulse effect selected.
6. Update flow: publish a fake higher release in a fork, point nothing — the repo URL is
   compiled in — so verify CheckAsync against the real repo's releases only; the swap path is
   best exercised by hand-crafting `pending.json` + a copy of the exe.
7. HID: import `docs/examples/casue-keyboard.probe.example.json`, Probe (read) with the rig's
   CASUE keyboard (`2A7A:939F`).

### Known limitations to state, not fix silently

- The companion page has no QR code (a QR encoder would need a dependency; the URL+PIN is
  copyable instead). **Reassessed in round 22:** a minimal QR encoder is ~200 lines of pure
  C# that emits SVG paths — no dependency needed — so this is worth doing next round for the
  phone-pairing flow. mDNS discovery is likewise not included — the Settings card lists the LAN URLs.
- Community protocols paint solid colours only (one report), and are not wired into the
  EffectEngine loop yet — deliberate: many OEM firmwares only accept slow polled writes, and
  streaming to them without capacity probing would repeat the Commander Core stall story.
- The time schedule and the rotation scheduler can both fire; the time rule wins its tick and
  the rotation countdown resets on any switch (documented in docs/scheduling.md).

---

## Round 22 (2026-09-23) — audit fixes: documented guarantees vs. real code

Seven issues where the code did not match what it (or the UI) promised, plus two low-priority
hardening items. All are fixed; `--rendertest` and `--uitest` pass with new regression tests.

| # | Issue | Fix |
|---|---|---|
| 1 | `TestPaintCommunity` asked ONE confirm dialog while `HidProtocolFile`'s safety model, `hid.writeTip` and `hid.explain` all promised "twice every time, one requiring typing" | Second confirmation is now a typed one: the device's `VID:PID` must be entered before every paint (`hid.paintWarn2`) |
| 2 | `ProfileShare.ImportCode` ran `GZipStream.CopyTo` with no ceiling — a ~6 KB pasted code inflates to gigabytes (one-line DoS on "Import share code") | Bounded copy: refuses past 4 MB inflated (regression test proves a 6 KB code → 6 MB payload is rejected) |
| 3 | `ScheduleRule.Matches` tested weekdays against the *current* moment, so `Sa 22:00-07:00` fired during Saturday's small hours (Friday's night) and `Fr 22:00-07:00` lost its Saturday-morning half | An overnight range is anchored to the night it STARTED on: on the morning half the weekday is checked against *yesterday*. Documented in the parser comment, `ts.hint` (en+fa) |
| 4 | `GameEventState`'s comment promised "volatile fields / Interlocked, never locks" but every member was a plain auto-property | Real lock-free backing fields: `Volatile` for the longs/string/bool, `Interlocked` on the bit pattern for the doubles (which also removes torn reads on 32-bit). Public API unchanged |
| 5 | `ControlHub`: `==` on the API token and PIN; unused `loopback` parameter; unbounded `Task.Run` per connection | `CryptographicOperations.FixedTimeEquals` for both; the dead parameter is gone; a `SemaphoreSlim(16)` caps concurrent connections so a LAN peer cannot hold sockets open and starve the pool |
| 6 | `AppUpdater.CleanupStale` deleted `FullRGB.exe.old` on the very next start, so a build that crashed on launch had no rollback path | `rollback.json` marker counts crash-free starts; `.old` survives until `RequiredHealthyStarts` (2) clean sessions (`NoteHealthyStart()` is called once the engine session is live) |
| 7 | `CommunityStore.Remove` built a path straight from a string | `IsSafeStoreName` guard (no separators, no `..`, no rooted path, must round-trip through `GetFileName`) |
| 8 | `/api/status` returned the dispatcher's raw text as JSON; throttling existed only on `/api/auth` | `JsonOrError` validates with `JsonDocument.Parse` first; the rate limit now covers every route (40 req/s), which also protects the UI thread from a dispatcher flood |
| 9 | `FindCollection` silently fell back to the first collection of a VID:PID | The fallback is reported: a log line for probes, a stronger dialog wording (`hid.paintWarnFallback`) for writes |
| 10 | **Clicking the Hardware tab crashed the app** (found by the user after the first dist36 build). `HidBridge` declared `HidD_GetCaps`, which exists in no Windows DLL — the HID parser routine is `HidP_GetCaps` and returns NTSTATUS, not BOOL. Same struct also had `Usage`/`UsagePage` swapped and only 4 of the 17 reserved ushorts, so the native call would have written past the managed buffer | Correct `HidP_GetCaps` + `HIDP_STATUS_SUCCESS`, corrected field order, full `Reserved[17]` (managed size now exactly 64 bytes). Two new build-gate checks: every `[DllImport]` entry point must resolve, and `HIDP_CAPS` must be 64 bytes |
| 11 | A failing page build reached the WPF dispatcher and terminated the process | `Tab_Changed` wraps `BuildHardwarePage()`; `BuildCommunityHidCard` wraps the HID enumeration. Both degrade to an inline error row |
| 12 | COMPATIBILITY.md credited the vendor HID channel (`usagePage 0xFF01`, 8-byte feature report) to the CASUE **keyboard** | Measured with `HidP_GetCaps`: the channel belongs to the **mouse** (`30FA:1140`). The keyboard exposes only standard collections with no feature report, so no protocol file can address it. Table corrected, and `_probe`-measured collection lists recorded |

Still open (deliberately, they are release-process work, not code fixes):

- **Authenticode code-signing of the released exe.** The update hash is self-referential, so it
  cannot prove a release came from us — only that the file did not change between download and
  apply. Signing is the real fix; documented in README → Security notes.
- **Automatic rollback.** `.old` is now preserved, but reverting is still manual (rename it back).
  A full version would health-check the new build and restore `.old` automatically.
- **QR code for companion pairing** — see the reassessment above; no dependency needed.

---

## 10c. Round 15 (2026-09-11) — v1.4.0: arena UI redesign


The React mock-up in `_arena-src/` (reviewed 2026-09-10, see the session memory) was ported into
the WPF UI and shipped as v1.4.0. Scope of the diff over v1.3.0 (11 files, ~1600 insertions):

- **Window 1280×900** (MinWidth 1080); `--uishot` renders at the real size now (was 560×700).
- **Sidebar always physically right**; only `MainCol`/`SidebarFlow`/`TopbarFlow`/`ActionBarFlow`
  get the RTL FlowDirection (glyphs never mirror).
- **Default accent `#A487EF` (violet)**, `OnAccent`/`AccentDim`/backdrop bloom retuned;
  `Language` default **fa** (Persian-first). All three defaults live in ProfileStore.cs AND
  App.xaml; the rendertest "corrupt falls back"/"bad accent" asserts follow.
- **Lighting page rebuilt**: hero card (live preview strip + caption), stats row
  (devices/LEDs/effect/sync), 18-tile effect grid with per-effect vector art strips
  (`BuildEffectArt`, ~46 px tall — StripFor/SegmentStrip/SpectrumBars/VuBars/ScannerCells/
  SparkleCells/EmberBar/CometStreak), connected-devices grid, power + quick-profile buttons.
- **Review defects fixed (all five from 2026-09-10):** quick-profile button label follows
  `SwitchProfile` (`ProfileQuickBtn.Content` set there, not only in ApplyLanguage);
  `EngineStateTxt` now shows diag.connected/diag.offline via `UpdateEngineCard()`;
  subtitle no longer snapped back to the Lighting text (`PageSubtitle()`/`UpdatePageHeading()`);
  `PowerBtn.Tag` + tooltip synced by `SyncPowerButton()` so the App.xaml Tag trigger works;
  new l10n keys (page.eyebrow.*, page.h1.*, hero.*, stats.*, effects.*, power.*, engine.*)
  in BOTH dictionaries (uitest parity gate passed).

Release mechanics: version bump 1.3.0→1.4.0 in csproj (line 14), published to **dist29**
(`1.4.0+<sha>`), autostart healed to dist29 (`--autostart=ensure` → taskExe=dist29, task-run
smoke test passed). Gates: rendertest ALL PASSED / uitest ALL PASSED / fxtest devices=4
framesSent=1684 errors=0 (DRAM 18–19 fps delivered, ASUS/Corsair 30 fps).

---

## 10d. Round 16 (2026-09-22) — the lighting rebuilds itself after sleep/hibernate

**User report (fa):** the app comes up correctly on boot and applies the effect, but after
hibernate/sleep the effects are not applied and the app does not work; closing it completely and
reopening it was the only fix.

**Diagnosis — two independent defects:**

1. **The wake-up notification never arrives.** `SystemEvents.PowerModeChanged` needs a message
   pump and is reproduced as *not firing at all* on Windows 11 after a manual sleep/wake
   (dotnet/runtime#78162, .NET 6/8: the OS never delivers `PBT_APMRESUMEAUTOMATIC` to the hidden
   window), and Modern Standby (S0) delivers nothing either — so the round-14 handler never ran.
2. **Even when it ran, the repair was wrong.** It restarted the engine only if `SdkAliveAsync()`
   failed. A post-suspend OpenRGB *keeps answering the SDK* (its server thread is healthy) while
   its USB/HID handles died with the suspend, so the probe reported "alive", no restart happened,
   and nothing reached the hardware. Closing the app worked because `CloseEngineOnExit`
   (default true) kills the engine on exit, so reopening necessarily started a fresh one.
   The old path also gave up after ONE attempt (a failed `ConnectAsync` left `_client`
   connected-but-dead with `_engine` null, and nothing retried).

**Fix (`MainWindow.xaml.cs`, plus `MainWindow.Settings.cs` and `L10n.cs`):**

- `HookPowerEvents` now runs a **5 s heartbeat** in addition to the (kept) power event. A
  `DispatcherTimer` cannot tick while the machine is suspended, so a gap of more than 4 × the
  interval (20 s) can only mean the machine slept. `Environment.TickCount64` counts sleep and
  hibernate time (`QueryUnbiasedInterruptTime` deliberately does not), and the wall clock is
  checked as well, so the detector keeps working if a future runtime makes TickCount64 unbiased.
  `IsResumeGap` is pure and asserted in `--rendertest` §40 (9 asserts: normal tick, late tick,
  exact boundary, 21 s, 8 h hibernate, unbiased tick + wall clock, backwards clock correction,
  zero interval).
- `RecoverAfterResumeAsync` → `TryRestoreLightingAsync`: **always** replaces the engine process
  (`EngineTask.EndTaskInstance` first when attached to the elevated-task engine, then
  `RestartAsync`, which kills leftovers and waits for the port to close — so a wedged
  post-suspend server can never be re-attached), disposes engine/client/WASAPI capture,
  reconnects (`ConnectAsync` → `OnConnected` → `StartEngine`) and re-applies the profile when
  effects were running or `AutoStartEffects` is set. **3 attempts** with 3/6/9 s backoff, with all
  blocking work (`schtasks`, teardown that joins writer threads) pushed off the UI thread. A
  second suspend arriving mid-repair is remembered (`_resumePending`) instead of dropped.
- `StartEngine` is re-entrant now: it disposes the previous WASAPI capture and builds the watchdog
  **once** (a rebuilt engine used to leak an audio capture and stack a second watchdog).
- `RescanAsync` returns early while a repair is in flight (it would fight the repair for the SDK
  session). Status strings added to BOTH dictionaries: `status.resume`, `status.resumed`,
  `status.resumeFailed`.

**Verification:** build 0 warnings; `--rendertest` ALL PASSED (167 asserts, incl. the 9 new ones);
`--uitest` ALL PASSED (l10n parity gate covers the three new keys). **NOT verified live:** the
wake-up repair itself still needs a real sleep/hibernate or an `NtSuspendProcess` freeze of the
GUI. Round 16 stopped at the gates because the user's `dist29` instance was left running (it holds
the single-instance mutex and the SDK session) and the go-ahead to close it never arrived. To
verify by hand: run `dist30\FullRGB.exe`, sleep/hibernate the PC, and watch the status pill —
it should show "Back from sleep — restarting the RGB engine..." and then "Lighting restored after
wake-up", with the effect back on. `--fxtest` was also not re-run in this round (same reason).

Artifact: `dist30\FullRGB.exe` (v1.4.0, 83.3 MB single file). Autostart still points at dist29;
`--autostart=ensure` re-points it to whichever copy is launched.

**Rebuild for the user's live test (2026-09-22 18:29):** version bumped to **1.4.1** and published to
`dist32\FullRGB.exe` (87.3 MB, md5 `cfbc9c1799b9dc4650cc29c51602ac19` — byte-identical to `dist31`,
so no source change since the 18:07 build). Gates re-run on the published exe: `--rendertest`
ALL PASSED (incl. the 9 `resume:` asserts) and `--uitest` ALL PASSED. Still not verified live —
the wake-up repair needs a real sleep/hibernate with the app running.

**UNPUSHED — verified 2026-09-22 18:35 (do this before any new work):** `origin/main` is still
`1d2b88c` (v1.3.0) and the remote has no `v1.4.0` tag; `gh release list` shows v1.3.0 as the latest
release. Rounds 15 + 16 (~1885 insertions over 13 files) therefore exist ONLY as uncommitted
working-tree changes on this machine — there is no backup. Sequence: commit → tag `v1.4.1` → push →
`gh workflow run windows-build.yml -f tag=v1.4.1` (never upload the 84 MB asset locally, §0).
`git status -sb` also shows the stale marker `main...origin/main [gone]`; re-run
`git branch --set-upstream-to=origin/main main` after the fetch.

---

## Round 20 (2026-09-23) — the machine watches itself: the independent lighting watchdog

**User report:** effects stop after a wake-up; the app itself looks alive (its own probes answer),
the LEDs are dark, and only "close and reopen" fixes it. Forensics confirmed the frozen session
live: `_probe/live_state.py` (READ-ONLY — reads the colours the SERVER holds, never writes one)
showed the running session had not moved a single stored colour since the 08:39 wake.

**Diagnosis — why rounds 16–19 could not catch it:**

Every recovery path so far asked the engine a question through the SAME client the app paints
with. A post-suspend engine keeps its server thread healthy and answers every probe
(`SdkAliveAsync`, controller counts, protocol version) while its USB/HID handles died with the
suspend. "The engine is alive" was reported truthfully; nothing was repaired.

The mechanism, proven twice on the real rig before any C# was written:
- `_probe/stall_probe.py` (A): a reader socket SEES the frames the app paints — readback works,
  so the test signal is real.
- `_probe/verify_stall.py`: the running session was ALREADY frozen (`baseline: painting=False`).
- `_probe/wedge_mech.py`: 40 clients that connect and never read their replies wedged the
  engine's writer threads — the exact freeze shape — and the server RECOVERED when they were
  released, so the fix does not need to restart the engine, only to notice.

**Fix — three new pieces (`Setup/`), all pure logic unit-tested:**

1. `PowerMonitor.cs` — decodes the raw `WM_POWERBROADCAST` wParam (0x04 suspend; 0x06/0x07/0x12
   resumes) and `WM_WTSSESSION_CHANGE` lParam (logon/logoff/lock/unlock/console) itself. The
   managed `SystemEvents.PowerModeChanged` event was reproduced as NOT FIRING on Windows 11 —
   this cannot be trusted again. `MainWindow` now hosts a hidden message window and acts on the
   decoded events (`BeginResumeRecovery` on resume; lock/unlock refresh the session clock).
2. `EngineShadow.cs` — probes the engine from the SIDE, never through the app's client:
   - TCP facts via `GetExtendedTcpTable` (iphlpapi) — is 6742 still LISTENING, how many
     ESTABLISHED clients. (A `/proc/net/tcp` read was tried first: that file is MSYS-only and
     does not exist for a native Win32 process — the Win32 API is the source.)
   - Per-device LED totals and a hash of the colours the engine HOLDS (readback, not our writes).
   - `Decide(...)` is pure: dead port → Repair; listening with nobody attached → Repair; zones
     back to 0 LEDs → Rebuild; TWO consecutive strikes of "no stored colour moved on an animated
     effect" → Repair (one strike only warns). Unreadable TCP table → no claim at all.
3. `LightingWatchdog.cs` — one threading-timer probe/minute (NOT a DispatcherTimer: it must tick
   while the UI thread is blocked, and a headless host has no message pump — the first version
   never fired in `--stalltest` because of exactly that). On a verdict it calls the same repair
   path a manual "close and reopen" uses. Opt-out checkbox: `settings.autorecover` (fa+en).

**`--stalltest` (new, `SelfTest.cs`)** — the end-to-end proof, headless, on the REAL engine:
joins the running engine, streams a real effect (frames stored in the hardware's colours are
verified by readback), wedges 40 non-reading clients, starts the watchdog, and REQUIRES the
watchdog to notice and act with no help. Verified live:

```
[stalltest] 40 non-reading clients attached
[stalltest] watchdog verdict Rebuild at 20.0s (listen=True conns=46 leds=233)
[stalltest] frames 784 -> 3608, hardware updating again: True
[stalltest] SUMMARY stallBuilt=True watchdogActed=True zonesStuck=False zonesRestored=True paintingAgain=True frames=3608
```

**Bugs found and fixed on the way (all mine):** WTS logon (0x6) missing from the decode table;
the one-strike rule first returned Wait instead of Ok; `DispatcherTimer` never ticked headless
(threading timer now); `/proc/net/tcp` does not exist for Win32 (GetExtendedTcpTable now);
`TcpRow` state codes are MIB values (LISTEN=2, ESTABLISHED=5), not /proc hex (0A/01); the port is
the LOW 16 bits of the packed address in network order (test vector first asserted the wrong
byte order); `SecondsSinceSession = 0` in the harness was read as "grace period" (now 300);
`Config/DeviceCache.cs` had never been committed (round 17) — it broke CI on the first push of
this round and is now in the repo.

**Verification:** `--rendertest` **ALL PASSED (230 asserts, +44)** — power decode, session decode,
watchdog verdicts, TCP facts, byte order, listener-only sessions; `--uitest` ALL PASSED; real-rig
`--stalltest` as above; CI (Debug build + headless gates) green after the DeviceCache fix.
`--fxtest` still not re-run (the user's GUI holds the SDK session). NOT verified live: a real
sleep/hibernate with the watchdog build running — the stalltest builds the same failure shape,
but an actual S3/S0 wake remains a by-hand test.

---

## Round 17 — the device inventory is remembered (v1.5.0)

The user's report: "settings should be saved once and the detected parts saved too, so it does not
scan from scratch every launch." Settings were already persisted (`ProfileStore` → `settings.json`);
what was missing was a memory of the HARDWARE. Three separate wins came out of one cache:

1. **The splash names the known parts before the engine answers.** `App.DeviceCache` is loaded in
   `OnStartup` (GUI path only — headless verbs must not touch user files) and the splash logs
   `scan.cached` immediately.
2. **The detect loop can finish early.** `StartupWindow.ContinueToEngineAsync` used a flat 5 s
   settle wait plus `stable >= 2 && i >= 4` (≈4.5 s of polling). With a remembered count the wait
   drops to 1.5 s and the `i >= 4` floor is waived once the count matches what we remember
   (`stable >= 2 && (i >= 4 || now == remembered)`). A PARTIAL answer never matches, so a cold boot
   still gets the full window and the 0-device restart guard is untouched. Net: ≈5 s off a warm
   launch, ≈0 when the hardware differs.
3. **A short scan can no longer delete the user's settings.** `Profile.PruneTo` only protected
   against a completely EMPTY scan; a cold boot that brought up three of four devices silently
   deleted the fourth one's overrides, calibration and LED counts. It now takes an optional
   `rememberedKeys` (the cache's `KnownKeys()`) and `MainWindow` passes it from `OnConnected` and
   the Rescan path. A device is only pruned once it has been gone for the whole retention window.

`Config/DeviceCache.cs` (new) — `CachedDevice`/`CachedZone` + `devices.json` next to `settings.json`.
Deliberately a SEPARATE file: settings are the user's data (backed up, exported, imported) while this
is a hardware observation that must be safe to delete, so a corrupt cache can never cost the user
their lighting configuration.

**Merge policy** (the part that has to be right): a device that answers is refreshed
(`LastSeenUtc`, `SeenCount++`, fresh zones); a device that stays silent is KEPT — that is the whole
point, a short scan must not erase identity; a device silent for `KeepMissingDays = 30` is swept, so
hardware removed for good does not keep its settings alive forever. Hard cap 200 devices.
`Merge` is **pure** (returns a new cache, never mutates the receiver) — an earlier version mutated
and returned `this`, which made the round-17 test save an already-swept cache and crash on
`Single()`; `--rendertest` now asserts the non-mutation too.
`Signature` = 16 hex chars of SHA-256 over `key|zoneCount|ledCount` lines, so "same hardware" is
distinguishable from "same device count".

**Verification:** build 0 warnings; `--rendertest` **ALL PASSED (186 asserts, +19)**: the merge
policy, retention sweep, signature, `MissingFrom`, `PruneTo` with and without remembered keys, and a
JSON round-trip incl. missing/corrupt files. `--uitest` ALL PASSED (l10n parity covers the three new
keys: `scan.cached`, `scan.cachedMatch`, `scan.cachedMissing`, in both dictionaries).
Artifact: `dist33\FullRGB.exe` (v1.5.0, 87.3 MB, md5 `aa9bebf83564855b48136c318c989aeb`), gates run
on the published exe. **Not yet verified live:** the early-exit path needs one real launch to write
`%APPDATA%\FullRGB\devices.json` and a second launch to take the fast path.

---

## Round 18 — a real installer (Inno Setup, per-user)

The user asked for an installer instead of the portable exe. `installer/FullRGB.iss` (Inno Setup 6,
already present on this machine at `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`).

Build:
```
ISCC.exe /DAppVersion=1.5.0 /DDistDir=..\dist33 installer\FullRGB.iss
→ installer\Output\FullRGB-Setup-<version>.exe
```
`AppVersion` and `DistDir` are #define-overridable so CI can build a release without editing the
file. Artifact: `installer\Output\FullRGB-Setup-1.5.0.exe` — 81,876,599 bytes, md5
`6c46a988a559f06a3d63476d83ead92d`.

**Design decisions**
- **Per-user install** (`PrivilegesRequired=lowest` → `{localappdata}\Programs\FullRGB`). The app is
  `asInvoker` and never needs admin, so the installer must not ask for UAC either. The RGB-RAM
  feature still raises its ONE UAC from inside the app — unchanged.
- **One file to install.** The 13 MB OpenRGB engine is embedded in the exe, so there is no vendor
  tree to lay down. `[Files]` copies `FullRGB.exe` + `README.md` + `LICENSE` and nothing else.
- **User data is deliberately outside `{app}`** (`%APPDATA%\FullRGB`, `%LOCALAPPDATA%\FullRGB`), so
  an update or uninstall cannot take it and `[UninstallDelete]` can remove `{app}` outright.
- **Languages:** English + Arabic + Turkish (the three Inno ships closest to the Persian-first UI).
  Persian is NOT bundled with Inno Setup; a line in `[Languages]` accepts `installer\Persian.isl`
  if one is ever added. The installer is English-by-default on purpose.
- `[Code]` uninstaller removes the two Scheduled Tasks the APP creates (`FullRGB` logon task,
  `FullRGB-Engine` elevated engine task) via PowerShell — Windows knows nothing about them, so
  without this an uninstall leaves a logon task pointing at a deleted exe (the exact 0x80070002
  stale-path bug `Autostart.EnsureCurrent` already self-heals).

**Two bugs found by actually running it** (both were mine, both are now fixed):
1. `Format('%.0f MB', [...])` — Inno's `Format` has no `%f`. Threw
   `Runtime error (at 9:916): Format '%.0f MB' invalid or incompatible with argument` inside
   `InitializeUninstall`, which ABORTED the entire uninstall (nothing was removed). Replaced with
   integer math (`DirSizeBytes` recursion + `IntToStr(Total div 1048576) + ' MB'`). Inno also needs
   helper functions declared before their callers, so `DirSizeBytes` precedes `DirSizeMb`.
2. **The uninstaller deleted the author's real `settings.json`, `devices.json` and `backups\`.**
   The data prompt was `MsgBox(..., MB_YESNO)` and `if Keep = IDYES then delete` — with
   `/SUPPRESSMSGBOXES` Inno answers the FIRST button, so an unattended uninstall silently chose
   "delete". Fixed with two hard rules, now in the script comments: an unattended uninstall
   (`UninstallSilent`) NEVER deletes user data and is never even asked; the interactive prompt
   defaults to **No** (`MB_DEFBUTTON2`) and returns early unless the user explicitly says yes.
   Deleting profiles/calibrations is now something a human must actively request.
   Lost in the test: settings.json, devices.json, backups\ (the `%LOCALAPPDATA%\FullRGB\engine`
   cache survived). `devices.json` rebuilds itself on the next launch; the settings did not.

**Verification (install → uninstall → install, all silent, to scratch dirs)**
- Install/verification log: `Installation process succeeded`, installed `FullRGB.exe` is
  byte-identical to `dist33\FullRGB.exe` (md5 `aa9bebf83564855b48136c318c989aeb`), Start Menu
  shortcut created, version metadata reads `FullRGB Setup / 1.5.0 / ScannerVpn`.
- Uninstall: `{app}` removed, Start Menu shortcut removed, **no `Runtime error` in the log**, and
  with seeded data in place `settings.json` + `devices.json` + `backups\` + the 27 MB engine cache
  all survived.
- The `RemoveTask` PowerShell snippet was executed against a non-existent task and returns cleanly
  (the uninstaller's path on a machine that never enabled autostart).
- **Not verified from here:** the task-removal half of the uninstaller could not be proven end to
  end — this agent session is unelevated and the Task Scheduler ACL refuses `Register-ScheduledTask`
  for anything it did not create, so a throwaway task could not be seeded, and `schtasks.exe` is on
  the sandbox's program blacklist. The command itself is verified clean (above); run the installer
  on a machine with autostart enabled to confirm the task disappears.
- **Collateral, repaired:** seeding a task named `FullRGB` overwrote the user's real logon task. It
  was re-registered to its exact original shape — `G:\Ai\RGB Control\dist29\FullRGB.exe`, no args,
  `-AtLogOn`, `-UserId Sajad -LogonType Interactive -RunLevel Limited`, workdir = the exe folder,
  `AllowStartIfOnBatteries`/`DontStopIfGoingOnBatteries`, `ExecutionTimeLimit=PT0S`,
  `MultipleInstances=IgnoreNew`, `StartWhenAvailable` — i.e. identical to
  `Autostart.BuildRegisterScript`. Note it points at **dist29** (the app rewrites it to whatever
  copy it is launched from, so running `dist33\FullRGB.exe` moves it forward on its own).

---

## 0. Published on GitHub

- Repo: <https://github.com/ScannerVpn/FullRGB> (public, MIT + GPL-2.0 notice for the bundled engine)
- Release: <https://github.com/ScannerVpn/FullRGB/releases/tag/v1.0.0> — `FullRGB.exe` 83.2 MB
  + `FullRGB.exe.sha256`
- CI: `.github/workflows/windows-build.yml` builds on `windows-latest`, runs `--rendertest` +
  `--uitest`, asserts the published exe is ≥78 MB (proves `engine.zip` is embedded — a build with
  the resource missing still succeeds and lands ~70 MB), uploads an artifact, and on
  `workflow_dispatch` with `tag=vX.Y.Z` creates/fills the release and un-drafts it.

**Never upload release assets from this machine.** A local `gh release upload` of the 84 MB exe
died with `wsarecv: An existing connection was forcibly closed` after 14 minutes and left the
release a draft with zero assets. Publish with:

```bash
cd "G:/Ai/RGB Control" && gh workflow run windows-build.yml -f tag=vX.Y.Z
cd "G:/Ai/RGB Control" && gh run watch <id> --exit-status
```

`--uitest` on a runner: two assertions describe the MACHINE, not the code, and are SKIPped when
`CI=true` — the runner's shell is elevated (so "not elevated when started normally" is false) and
its VM has no USB tree (so `usbscan` and the offline-unknown classification have nothing to
classify). Both still run and pass on this desktop. Simulate a runner locally with
`$env:CI='true'` before `--uitest`.

---

## 1. What this app is

Windows desktop app (WPF, .NET 8, single-file self-contained EXE) that finds every RGB device
attached to the PC and applies software effects to all of them at once, with no plugins for the user
to install. Persian/English UI (RTL aware), user-selectable accent colour on a dark theme, compact
single-column layout, tray icon, autostart, profiles.

**Engine:** bundled OpenRGB (portable, `vendor\OpenRGB\OpenRGB Windows 64-bit\`) launched
headlessly as an SDK server; FullRGB speaks the OpenRGB binary protocol directly (own client,
no third-party SDK package).

**Reference rig (user's PC):** ASUS ROG MAXIMUS Z790 DARK HERO, Corsair Commander Core (pump + 6 fan
ports), 2× KLEVV/ENE DDR5 DIMMs, NVIDIA RTX 4070 Ti SUPER (Zotac, subsystem `19DA:7675`).

---

## 2. Verified hardware state (what actually works)

| Device | Kind | LEDs | Zones | Status |
|---|---|---|---|---|
| ASUS ROG MAXIMUS Z790 DARK HERO | Motherboard | 481 | Aura Mainboard (1, fixed) + Addressable 1‑4 (120 each, resizable) | ✅ paints, 30 fps |
| Corsair Commander Core | Cooler | 233 | Pump (29, fixed) + RGB Port 1‑6 (34 each, resizable) | ✅ paints, ~3 fps ceiling (hardware) |
| ENE DRAM ×2 (DDR5) | DRAM | 8 each | DRAM (8, fixed) | ✅ paints, ~20 fps — **needs the elevated engine task** (§3a) |
| RTX 4070 Ti SUPER | GPU | — | — | ❌ **no OpenRGB controller for this card** (see §7) |
| USB GAMING MOUSE (`30FA:1140`, mfr INSTANT) | Mouse | — | — | ❌ **no OpenRGB driver for this PID** (see §3b) |
| CASUE USB KB (`2A7A:939F`) | Keyboard | — | — | ❌ **no OpenRGB driver for this PID** (see §3b) |

Totals: **714 paintable LEDs** without the engine task, **730** with it (+ 2×8 DRAM).
The two unsupported peripherals are listed in the app itself, on the Hardware page, with the reason.

---

## 3. Round 7 — bugs found and fixed

Every item was found by reading the code or by looking at rendered screenshots; each has a test or a
screenshot proving the fix.

### Engine / effects (logic bugs)

1. **`SyncZones = false` did nothing for 7 of 8 effects.** Only `Rainbow` used the per-zone seed;
   wave, blink, breathing, custom, temperature and audio ignored it, so "offset per zone" was a
   no-op. The seed now shifts BOTH time and LED position in one shared phase model
   (`t = ctx.Time + seed*0.37`, `spatial = seed*5`). Covered by `--rendertest` §15 for every
   animated effect.
2. **Custom-palette zones re-aligned.** `Custom` floors a palette index, so a time-based zone offset
   could land on a multiple of the palette length and look synced again. It now uses the base time
   plus an integer offset guaranteed to be a non-zero residue mod palette length.
3. **Wave wavelength was relative to zone size** (`ledCount/2`), so a 120-LED header and a 34-LED fan
   showed different physical wave sizes from the same settings. Capped at 30 LEDs — one wave length
   everywhere.
4. **Frame pacing was wrong.** The loop slept a flat 33 ms *after* render+IO, so the real rate was
   ~24 fps and drifted with load. Now it sleeps the remainder of each frame against a monotonic
   deadline and resyncs if it falls behind. `EffectEngine.Fps` exposes the measured rate.
5. **Sensor reads blocked the render loop.** `LibreHardwareMonitor.Update()` can block tens of ms and
   ran inline every 500 ms. It now runs on its own thread (`SensorLoop`); the render loop only reads
   two `double?` fields. `TemperatureProvider` is also lock-guarded (Update is not thread-safe) and
   its `Dispose` is idempotent.
6. **Audio "bands" were not frequency bands.** `AudioProvider` split the buffer by SAMPLE POSITION —
   i.e. time — so bass/mid/treble were the same signal delayed. Replaced with a real windowed
   radix-2 FFT (1024-point Hann), bands 0–250 / 250–4000 / 4000–16000 Hz, per-band gains.
   `--rendertest` §18 asserts a pure 64-bin tone peaks in bin 64.
7. **Audio only handled 32-bit float and 16-bit PCM.** Now handles 8/16/24/32-bit and detects
   IEEE-float vs integer from the mix format; a truncated final frame can no longer read past the
   buffer.
8. **The music effect always followed overall volume.** `EffectDef.AudioBand` (level/bass/mid/treble)
   is selectable per effect.
9. **SDK request/response could interleave.** `Send` and `Request` took *different* locks, so a
   render-loop write could slip between a request's write and its read and desync the protocol
   stream. One `_io` lock now covers every socket operation.
10. **~10 MB/minute of garbage.** Every zone frame allocated a fresh payload array (30 fps × 12
    zones). Now a single growable buffer is reused under the IO lock.
11. **`TcpClient.Connect` could hang ~20 s** on a dead port; bounded to 5 s.
12. **`RefreshControllers` trusted the device count** from the wire (a bad reply meant a 4-billion
    iteration loop); clamped to 512.
13. **`ExpandAllZones` tried to resize fixed zones** (harmless but noisy) and ignored cancellation
    mid-loop. Both fixed.

### Config / lifecycle

14. **Settings had no validation.** A hand-edited or older `settings.json` with a duplicate profile
    name, a blank name, a bad language, port 0 or a garbage accent broke the profile ComboBox and the
    tray submenu. `AppSettings.Normalized()` repairs all of it on load (`--rendertest` §21).
15. **A crash between write and rename lost settings.** `LoadFrom` now recovers `settings.json.tmp`.
16. **Profiles grew forever.** Overrides/calibrations/zone sizes for devices that are no longer
    present are pruned on connect and rescan (`Profile.PruneTo`, `--rendertest` §20).
17. **`Autostart.Set` returned true even when `schtasks` failed** (it only checked that the process
    started). It now waits and checks the exit code.
18. **"Start minimized" did not rewrite the scheduled task**, so the `--minimized` argument went
    stale until autostart was toggled off and on.
19. **Blackout left the frame-dedupe cache populated**, so restarting the same effect could skip the
    first frame and leave zones dark.

### UI bugs (found by rendering the windows and inspecting the pixels)

20. **`U+E9CB` does not exist in Segoe MDL2 Assets** — the "Temperature" effect tile was drawing an
    empty tofu box. Verified the entire icon set against the font's `CharacterToGlyphMap`;
    `--uitest` now fails the build if any codepoint is missing. Four effects (fire, comet, wave,
    rainbow) have no reasonable MDL2 glyph at all and are drawn as vector `Path` geometry instead.
21. **11 px MDL2 glyphs in the 31×29 icon buttons were an unreadable smudge**, and `IconBtn`
    inherited `Btn`'s `13,7` padding, pushing the glyph off-centre and clipping it. Now 13 px with
    zero padding, and `Btn` respects `TemplateBinding Padding`.
22. **Accent recolouring silently did nothing.** Brushes that come from BAML are FROZEN, so mutating
    `SolidColorBrush.Color` threw nothing and changed nothing. `Theme.ApplyAccent` now REPLACES the
    resource entries and every accent consumer uses `DynamicResource`.
23. **The Devices tab was unusable**: it depended on a selection made on the *Lighting* tab, so it
    normally showed an empty card with a header and no rows. It now has its own device picker plus a
    real empty state (icon + message + Rescan, help text auto-expanded).
24. **The splash had no way out** — a stuck engine start meant Task Manager. Added a close button that
    cancels startup cleanly.
25. **Diagnostics printed placeholder junk** (`Engine: - · protocol v0 · 0 fps`). It now reads
    "Engine not connected" until the SDK is up, and only shows fps while the engine runs.
26. **`Preview` timer was recreated on every editor rebuild** and kept ticking while the window was
    hidden in the tray. One timer for the window's lifetime, skipped when `!IsVisible`.
27. Ragged form: colour pills, sliders and combo boxes each ended at a different x. Every control
    column now stretches to one right edge, and the effect tiles sit in a `UniformGrid` that shares it.
28. **Accent selection was a white ring** — invisible on the white swatch. Now a contrast-tinted
    check inside the swatch.
29. Colour picker: the SV cursor sat outside the clip at S=V=1 (invisible), the hex row and presets
    had different gutters, and ~100 px of dead space sat under the buttons. Cursor is clamped inside
    the field with a dark halo, all rows share the field's width, and the dialog sizes to content.
30. `MessageBox`/`ColorDialog` (grey Win32 boxes) replaced with themed `PromptDialog`,
    `ConfirmDialog` and a real HSV `ColorPickerDialog`.

---

## 3a. Round 9 — RGB RAM without making the app run as admin

**Symptom the user reported:** "رم هارو شناسایی نمیکنه" (it doesn't detect the RAM) — the device
list showed 2 devices / 714 LEDs.

**Root cause, straight out of the engine's own log (`%APPDATA%\OpenRGB\logs`):**

```
unelevated engine:  Start PawnIO: SmbusI801.bin
                    ERROR: Permission Denied, PawnIO initialization aborted
                    -> [ASUS ...] + [Corsair ...]                       = 2 controllers
elevated engine:    Start PawnIO: SmbusI801.bin
                    PawnIO initialized successfully
                    -> [ENE DRAM] x2 + [ASUS ...] + [Corsair ...]       = 4 controllers
```

RGB DIMMs live on the SMBus, the SMBus needs the PawnIO kernel driver, and PawnIO only opens from
an elevated process. Round 8 removed the app's self-elevation (as requested), which is exactly
what took the RAM away. Windows constraint, not a FullRGB bug.

**Fix — elevate the ENGINE, never the app** (`Setup\EngineTask.cs`):

- One-time: register the bundled `OpenRGB.exe` as Scheduled Task **`FullRGB-Engine`** with
  `RunLevel=Highest` and **no trigger** (run-on-demand). That registration shows a UAC prompt
  exactly once, ever.
- Every launch after that: `OpenRgbProcessManager.StartAsync` calls `schtasks /Run /TN FullRGB-Engine`
  → the Task Scheduler service starts the engine elevated with **no prompt**, FullRGB attaches to
  the SDK port as a normal user. Verified: `FullRGB.exe` handle opens fine from an unelevated
  shell (not elevated), while `Stop-Process` on the engine returns *Access is denied* (elevated).
- If the task is missing or fails, the old plain-launch path still runs, so nothing regresses for a
  user who never enables it.
- Turning it OFF also kills the elevated engine (one UAC prompt): otherwise the old engine keeps
  the SDK port and FullRGB would just re-attach, making the setting look broken.
- UI: **Hardware page → "Unlock more hardware" → "Enable RGB RAM"** (round 9 put this in
  Settings → Advanced; round 10 moved it where the user actually looks). The row states honestly
  whether DRAM controllers actually appeared (`adv.ram.on` vs `adv.ram.onNoRam`), instead of
  claiming success from the mere existence of the task.
- Removed the old "Restart as admin" path and its three l10n keys — it made the whole app elevated,
  which the user explicitly does not want.
- New headless verb for testing without the GUI:
  `FullRGB.exe --enginetask=status|register|run|remove`.

**Verified end-to-end on the rig:** `--enginetask=register` → `register: OK`, task reports
`RunLevel=Highest`, `Arguments=--server --server-port 6742`; `--enginetask=run` → engine up,
log shows `PawnIO initialized successfully` + two `[ENE DRAM] Registering RGB controller`;
`--fxtest` now reports **devices=4**, `framesSent=1684 errors=0`, and the GUI header reads
**"4 devices · 730 LEDs"** with both `ENE DRAM · 8 LEDs · Zones (1)` rows on the Devices page.

**DRAM specifics (probed, `_probe\dram_modes.py`, `_probe\dram_write_cost.py`):**
- Modes: Direct (active), Off, Static, Breathing, Flashing, Spectrum Cycle, Rainbow, Chase Fade,
  Chase, Random Flicker. Direct is `colorMode=PER_LED`, so per-LED writes are honoured; zone
  writes read back exactly (`applied = True`).
- `fxtest` prints `direct=False` for them only because `InDirectMode` compares against the mode
  named "Direct" and the DIMMs report active index 0 with that name — the flag is cosmetic here,
  writes work.
- One 8-LED zone write costs ~24–40 ms → ~20 fps ceiling per DIMM. A whole-device `UPDATE_LEDS`
  is a **server-side no-op** for them too (B/A = 1.14 and 1.21 with 10 extra writes), so per-zone
  writes stay mandatory.

Two bugs found while building this, both silent:
- `schtasks /XML` declares `encoding="UTF-16"` in its prolog but writes SINGLE-BYTE text when the
  output is redirected (verified with `od`). Forcing `StandardOutputEncoding=Unicode` produced
  garbage, so the task looked unregistered. `QueryXml` now reads raw bytes and picks whichever
  decoding actually contains `<Command>`.
- The task stores an **absolute** exe path, so a task from another install (dist8 vs dist9) would
  silently start the wrong engine. `MatchesInstall` is now what gates the task path, and the UI
  offers "Re-register" when it does not match.

---

## 3b. Round 10 — engine inside the app, and an honest hardware page

Three things the user asked for: the engine should be a *plugin inside the program*, the RAM setup
step should be **visible on first run** rather than buried in Settings → Advanced, and the mouse
should be checked for RGB support.

**1. The engine now ships INSIDE `FullRGB.exe`.**

`FullRGB.csproj` zips `vendor\OpenRGB\OpenRGB Windows 64-bit\` at build time and embeds it as the
managed resource `FullRGB.engine.zip` (13.2 MB compressed). `SDK\EngineBundle.cs` unpacks it into
`%LOCALAPPDATA%\FullRGB\engine\<sha256[..12]>\` on first use — hash-named, so a new engine version
lands in a new folder instead of half-overwriting the old one, with a `.complete` marker so a
half-extracted folder is redone rather than trusted. `OpenRgbProcessManager.DefaultExePath()` prefers
the unpacked copy and still falls back to a `vendor\` folder next to the exe.

Result: `dist11\` (and earlier `dist10\`) is **one 84 MB `FullRGB.exe`** and nothing else. No OpenRGB folder, no separate
program the user can see or launch.

MSBuild traps hit on the way (both produced a green build with a MISSING resource):
- `$(IntermediateOutputPath)` is EMPTY in a `PropertyGroup` evaluated before the SDK targets are
  imported, so the zip silently landed in the project root. Use `$(BaseIntermediateOutputPath)`.
- `BeforeTargets="CoreCompile"` is TOO LATE to contribute an `EmbeddedResource`: raw resources are
  translated during `PrepareResources`. Use `BeforeTargets="AssignTargetPaths"`.
- Keep `Inputs/Outputs` on the zip target only. `AddEngineResource` must have none, or an
  up-to-date `PackEngine` skips the item contribution too.

**2. New HARDWARE page in the nav rail** (`MainWindow.Hardware.cs`, `Diag\*`).

`Diag\UsbScan.cs` enumerates present USB/HID devices through SetupAPI and reads
`DEVPKEY_Device_BusReportedDeviceDesc` — the string the DEVICE reports ("USB GAMING MOUSE"), not the
driver name ("USB Input Device"), which is what makes the list recognisable. No elevation needed.

`Diag\SupportMatrix.cs` pairs engine controllers with those devices **by VID:PID**, extracted from
the controller's `location` field. Name matching was tried first and failed on real data: the engine
says "ASUS ROG MAXIMUS Z790 DARK HERO" where Windows says "AURA LED Controller". Verified `location`
formats on this rig: `HID: \\?\HID#VID_0B05&PID_18F3&MI_02#...` for USB, `I2C: i801, address 0x71`
for the DIMMs.

The page shows the engine card (what it is, how it was reached, whether it has SMBus access, bundle
size, protocol version, a button to its log folder), then *Controlled by FullRGB* / *Needs one setup
step* / *Detected, not controllable* groups with each device's VID:PID and a one-line reason, then
the PawnIO + RGB RAM setup rows (**moved out of Settings entirely** — they live here only), then
"Why is a device missing?".

The SMBus line is decided by EVIDENCE (are DRAM controllers present?), not by which launch path was
used: an engine we merely attached to can already be elevated, and an engine started via the task
can still fail PawnIO.

**A dishonesty bug the headless `--uishot` pass exposed.** With no SDK connection there is no
controller list, so every USB device fell into "Detected, not controllable" — the page claimed the
ASUS board and the Corsair hub were unsupported, which is false. Added `SupportState.Unknown` plus
an `engineConnected` argument: with no connection the group is *"Detected, support unknown"* and the
reason is *"the engine is not connected, so its lighting support is unknown"*. Locked in by a
`--uitest` assertion that Build(engineConnected: false) yields NO `Unsupported` rows and at least
one `Unknown`, while `engineConnected: true` yields no `Unknown` rows.

**3. The mouse: detected, and it is NOT controllable.**

`30FA:1140`, product string "USB GAMING MOUSE", manufacturer "INSTANT" (an OEM Sinowealth-class
controller). Probed with `_probe\hid_mouse_probe.py`:

```
MI_00        usagePage 0x0001 usage 0x0002   (the mouse itself)
MI_01 COL01  usagePage 0x0001 usage 0x0006   (keyboard collection)
MI_01 COL02  usagePage 0x000C                (consumer control)
MI_01 COL03  usagePage 0xFF00  in=3          (vendor-defined)
MI_01 COL04  usagePage 0xFF01  feature=8     (vendor control channel)
MI_01 COL05  usagePage 0x0001 usage 0x0080
```

So the hardware *does* have a vendor channel, and `HidD_GetFeature` on COL04 answers on report id 7
(`07 00 7C 07 59 00 03 6F`) and refuses ids 0–6. But OpenRGB has **no driver for this PID** — its
2362 detectors include "Sinowealth Keyboard" and 27 other "USB Gaming Mouse" entries, none of them
30FA:1140 — and its HID pass registered only the ASUS board and the Corsair hub.

Deliberately NOT done: guessing SET_FEATURE payloads to find the colour command. Writing invented
bytes to unknown mouse firmware can corrupt its configuration flash, and that is not a risk worth
taking on the user's hardware. `_probe\mouse_feature_read.py` is read-only for this reason. Adding
real support means writing an OpenRGB device driver for this PID after capturing its vendor tool's
USB traffic.

Same conclusion for the keyboard, `2A7A:939F` "CASUE USB KB": present, no driver, no vendor
collection at all (only keyboard/consumer/system collections).

---

## 4. New in round 7 (features)

- **Four new effects:** `Gradient` (static primary→secondary ramp), `ColorCycle` (whole strip cycles
  hue), `Comet` (travelling dot with fading tail), `Fire` (deterministic hash-noise flicker — no
  shared `Random`, so it is thread-safe and reproducible). Enum values are explicit and APPEND-only,
  because effect types are serialized as numbers.
- **Per-zone effect overrides.** `Profile.ZoneOverrides` (keyed `deviceKey|zoneIndex`) beats the
  device override, which beats the global effect. Zone rows appear nested under the selected device
  on the Lighting page; the device row menu can clear them in bulk.
- **Accent theming.** 8 presets + a custom colour picker, applied live to the whole UI.
- **`AutoStartEffects` setting** — the app can come up without painting.
- **Custom window chrome** (`WindowChrome`, so Windows keeps snap/resize) with minimize / maximize /
  close, a live status pill in the title bar, and a vertical nav rail instead of a tab strip.
- **Two new headless modes:** `--uitest` (instantiate every window/dialog, check every resource key,
  every icon codepoint, and that en/fa define the same keys) and `--uishot` (render the windows to
  PNG for layout review without launching the elevated app).

---

## 5. Protocol facts (hard-won — do not re-derive)

- Launch flags are **exactly** `--server --server-port 6742`. `--serverport` ⇒ instant crash
  `0xC0000409`. A plain launch shows the GUI and starts **no** SDK server.
- Header is **16 bytes**: `"ORGB"` + `u32 device_id` + `u32 packet_type` + `u32 payload_size` (LE).
- Handshake: `REQUEST_PROTOCOL_VERSION=40` with `u32 4`; a silent server (1.5 s timeout) ⇒ v0.
  Negotiated version = `min(serverMax, 4)`. Then `SET_CLIENT_NAME=50` (NUL-terminated).
- `REQUEST_CONTROLLER_DATA=1` payload layout is documented in `DeviceParser.cs` and parses to an
  exact end-of-payload fit on this rig. Strings are `u16 length INCLUDING the NUL` + bytes.
  Modes carry **12 fixed u32s** after the name (a 10-u32 assumption corrupts the parse).
- `UPDATE_LEDS=1050` payload: `u32 total_size(includes itself) + u16 count + count×(R,G,B,pad)`.
  The `u32` size prefix is **required** — without it the server aborts the connection after ~2 frames.
- `UPDATE_ZONE_LEDS=1051` payload: `u32 total_size + i32 zone_index + u16 count + count×RGBA`.
- `RESIZE_ZONE=1000` payload: `i32 zone_index + i32 new_size`.
- `UPDATE_MODE=1101` payload: `u32 total_size + i32 mode_id + str name + value + flags +
  speed_min/max + [v≥3 brightness_min/max] + colors_min/max + speed + [v≥3 brightness] +
  direction + color_mode + u16 color_count`.
- **Every addressable zone reports `leds_count=0` on a fresh connect** and must be resized before it
  can be painted (`ExpandAllZones`, 120 ms between resizes, then re-enumerate).
- 30 fps streaming to both devices is stable indefinitely (~700 frames per 14 s, 0 errors).

## 5b. WPF facts (hard-won)

- **Brushes loaded from BAML are frozen.** Runtime theming must replace `Application.Resources[key]`
  and consumers must use `DynamicResource`; mutating a frozen brush is a silent no-op.
- **`x:Name` inside a `ControlTemplate` does not create a code-behind field.** The status pill is a
  plain `Border` for exactly this reason.
- **`MainWindow` inside `App` resolves to `Application.MainWindow`**, not the class — qualify it as
  `FullRGB.MainWindow`.
- **`U+E9CB`, `U+E9BF`, `U+E9D3` are not in Segoe MDL2 Assets.** Check codepoints against
  `GlyphTypeface.CharacterToGlyphMap` (`GlyphCheck.cs`) rather than trusting an icon list.
- WinForms + WPF in one project makes `Color`, `Brushes`, `Size`, `Point`, `TextBox`, `Image` and
  `FontFamily` ambiguous — every UI file needs explicit `using X = System.Windows...` aliases.
- `--uitest`/`--uishot` must set `MainWindow.Headless = true`, or `Window.Show()` runs the real
  `Loaded` handler, starts OpenRGB and blocks on a UAC prompt forever.

---

## 6. Code map

```
G:\Ai\RGB Control\
├─ PLAN.md                     ← this file
├─ vendor\OpenRGB\OpenRGB Windows 64-bit\   ← engine SOURCE tree; zipped into the exe at build
├─ dist11\                     ← current publish: ONE FullRGB.exe (84 MB), no vendor folder (dist10 retained)
├─ tools\make_icon.py          ← generates Assets\app.ico
├─ tools\verify.sh             ← runs rendertest + uitest + fxtest and prints each result
├─ tools\grab.ps1 / uiclick.ps1 / anim.ps1   ← GUI verification (PrintWindow, UIA click, animation)
├─ _probe\                     ← protocol probes; solid_diag.py and hold_diag.py disproved the
│                                "our writes are wrong" theory — keep them.
│                                dram_modes.py / dram_write_cost.py: DIMM capability + cost
│                                hid_mouse_probe.py: HID collections of mouse/keyboard
│                                mouse_feature_read.py: READ-ONLY vendor feature report probe
│                                controller_location.py: what the engine reports as `location`
└─ src\FullRGB\
   ├─ FullRGB.csproj           single-file win-x64; PackEngine/AddEngineResource embed engine.zip
   ├─ app.manifest             asInvoker — the app NEVER self-elevates (round 8)
   ├─ App.xaml                 DESIGN SYSTEM: palette + every stock control retemplated
   ├─ App.xaml.cs              startup: --selftest / --fxtest / --rendertest / --uitest / --uishot /
   │                           --usbscan / --enginetask[=register|remove|run]
   │                           → single-instance mutex → StartupWindow → MainWindow
   ├─ Theme.cs                 runtime accent theming (replaces resources; HSV rotate/mix/luminance)
   ├─ L10n.cs                  en/fa dictionaries + MissingKeys() completeness hook
   ├─ Dialogs.cs               themed PromptDialog + ConfirmDialog
   ├─ ColorPicker.cs           themed HSV picker (SV field + hue strip + hex + presets)
   ├─ GlyphCheck.cs            every MDL2 codepoint the UI uses + font-existence check
   ├─ TrayController.cs        always-on notification-area icon + live menu
   ├─ RenderTests.cs           --rendertest: pure-logic assertions
   ├─ UiTests.cs               --uitest: windows/dialogs, resources, glyphs, l10n, engine bundle,
   │                           USB scan, support matrix
   ├─ UiShots.cs               --uishot: render windows to PNG (--out=DIR, --fa)
   ├─ StartupWindow.xaml(.cs)  splash: deps → engine → detect-until-stable → zones → hand off
   ├─ MainWindow.xaml          custom chrome + nav rail + 4 pages + sticky action bar
   ├─ MainWindow.xaml.cs       lifecycle, connection, status, banners, tray, language
   ├─ MainWindow.Devices.cs    lighting-page target list (global / device / zone rows)
   ├─ MainWindow.Zones.cs      devices page: picker, zone sizes, Identify, calibration
   ├─ MainWindow.Effects.cs    effect catalog (glyph or vector path), params, hero preview
   ├─ MainWindow.Hardware.cs   HARDWARE page: engine card, per-device support groups
   ├─ MainWindow.Settings.cs   action bar, profiles, accent, startup, Autostart, RefreshAdvanced
   ├─ Diag\UsbScan.cs          SetupAPI enumeration of present USB/HID devices (no elevation)
   ├─ Diag\SupportMatrix.cs    pairs engine controllers with USB devices by VID:PID; explains gaps
   ├─ SelfTest.cs              --selftest (solid paint) and --fxtest (REAL engine path)
   ├─ Setup\DependencyManager.cs  PawnIO detect/download (pinned 2.2.0 + SHA-256)/silent-install
   ├─ Setup\EngineTask.cs      elevated on-demand Scheduled Task for the engine (RGB RAM)
   ├─ SDK\EngineBundle.cs      engine.zip embedded resource → LocalAppData, hash-named, once
   ├─ SDK\OpenRgbProcessManager.cs  start/attach/restart OpenRGB, port wait, log tail, SMBus check
   ├─ SDK\OpenRgbClient.cs     protocol client (one IO lock, reused buffers, bounded connect)
   ├─ SDK\DeviceParser.cs      controller-data parser
   ├─ SDK\Models.cs            RgbController/RgbMode/RgbZone, RgbDeviceType, Pkt constants
   ├─ Config\ProfileStore.cs   Profile (+ZoneOverrides) + Calibration + AppSettings.Normalized()
   ├─ Effects\Effects.cs       EffectDef + EffectRenderer (12 effects, shared phase model)
   ├─ Effects\EffectEngine.cs  fixed-rate 30 fps loop, sensor thread, per-zone render, dedupe, revive
   └─ Sensors\SensorProviders.cs  LibreHardwareMonitor temps (locked), WASAPI loopback + FFT
```

## 6b. UI design system

- **Palette**: `Bg #080A0F`, `BgElevated #0D111A`, `Card #121822`, `Surface #1A2331`,
  `Border #243040`, `Text #EAF1F8`, `Muted #8593A4`, `Faint #556274`, accent **user-selectable**
  (default `#00E5FF`), `Ok #3DDC97`, `Warn`, `Danger`. `AppBackdrop` is a soft radial bloom.
- **Custom chrome** via `WindowChrome` (keeps Windows snap/resize/shadow, unlike
  `AllowsTransparency`): brand ring, title, live status pill (dot + text + refresh glyph, click to
  rescan), language toggle, min/max/close.
- **Vertical nav rail** (Lighting / Devices / Hardware / Settings) with an accent rail on the active item.
- **Lighting page** leads with a hero card: an animated 128-LED preview strip (rendered by the SAME
  `EffectRenderer` the engine uses, 20 fps) with the effect name and target over a scrim, then the
  12-tile effect grid (`UniformGrid`, 4 columns) and the parameter form.
- **Effect icons**: MDL2 glyph where a good one exists, otherwise vector `Path` geometry bound to the
  tile's `Foreground` so it follows hover/selection. Never add an icon without adding it to
  `GlyphCheck`.
- **Devices page**: own device picker, zone LED-count inputs (digits only, min–max tooltip),
  Identify, per-device colour correction, and a real empty state when nothing is detected.
- **Hardware page** (round 10): the engine card (what the engine is, how it was reached, whether it
  has SMBus access, how big the embedded bundle is, protocol version, a link to its log folder),
  then every device grouped as *Controlled by FullRGB* / *Needs one setup step* / *Detected, not
  controllable* with its VID:PID and a one-line reason, then the two setup rows (PawnIO, RGB RAM),
  then "Why is a device missing?". This page is the ONLY place the setup rows live.
- **Settings page**: profiles (rename/new/delete with themed dialogs), accent swatches, startup
  toggles, About + a diagnostics line that never shows placeholder values.

---

## 7. Known limitations (state these to the user, don't "fix" them silently)

- **RTX 4070 Ti SUPER lighting is not controllable.** OpenRGB registers the *NvAPI I2C interface*
  for the GPU but has **no controller** for this board (Zotac `19DA:7675`). Not a FullRGB bug.
- **The mouse (`30FA:1140` "USB GAMING MOUSE", mfr INSTANT) and keyboard (`2A7A:939F` "CASUE USB KB")
  cannot be lit.** Both are present and listed on the Hardware page; OpenRGB has no driver for
  either PID. The mouse does expose a vendor HID channel (COL04, usagePage `0xFF01`, 8-byte feature
  report), so support is *possible* but requires writing a driver from captured USB traffic — do NOT
  guess SET_FEATURE payloads at unknown mouse firmware.
- **RAM lighting requires an elevated ENGINE** (not an elevated app). Without the `FullRGB-Engine`
  task, runs log `Permission Denied, PawnIO initialization aborted` and expose 2 devices; with it,
  4 devices / 730 LEDs. PawnIO **is already installed** on this machine.
- `SmbusIntelSkylakeIMC.bin` always aborts with `code=-2147024841` even elevated — DDR5 IMC path is
  unsupported by that PawnIO module; DRAM is still found over the i801 bus, so this is harmless.
- Addressable zones default to their **maximum** LED count (120 per header, 34 per fan port). Enter
  the real per-port counts on the Devices page so wave/comet spacing is physically correct;
  `Identify` flashes one zone white to find which port is which.
- iCUE must be closed for Corsair control (banner + close button; iCUE services may restart it).
- OpenRGB `--list-devices` prints nothing on Windows (GUI subsystem) — always use the SDK.
- The publish folder is locked while an elevated `FullRGB.exe` from it is running; publish to a new
  `PublishDir` or have the user exit via the tray first.
- `--uishot` renders windows off-screen; it cannot capture popups (ComboBox drop-downs, context
  menus, tooltips) because those live in separate top-level windows.

---

## 8. Rerunnable commands

```bash
# build
cd "G:/Ai/RGB Control/src/FullRGB" && dotnet build -c Debug -v q --nologo

# ALL gates at once (rendertest + uitest + fxtest)
cd "G:/Ai/RGB Control" && bash tools/verify.sh Debug

# individual gates (PowerShell redirect is required: the EXE is a GUI subsystem binary)
powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath 'G:\Ai\RGB Control\src\FullRGB\bin\Debug\net8.0-windows\win-x64\FullRGB.exe' -ArgumentList '--rendertest' -RedirectStandardOutput \$env:TEMP\rt.txt -PassThru -NoNewWindow; \$p.WaitForExit()"
tr -d '\000' < "$LOCALAPPDATA/Temp/rt.txt" | grep -aE "FAIL|PASSED"   # output is UTF-16

# screenshots for layout review (add --fa for the RTL pass, --out=DIR to choose a folder)
powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath 'G:\Ai\RGB Control\src\FullRGB\bin\Debug\net8.0-windows\win-x64\FullRGB.exe' -ArgumentList '--uishot' -RedirectStandardOutput \$env:TEMP\shot.txt -PassThru -NoNewWindow; \$p.WaitForExit(90000)"
ls "$LOCALAPPDATA/Temp/fullrgb-shots"

# regenerate the app icon after editing the design
cd "G:/Ai/RGB Control/tools" && python make_icon.py

# protocol diagnostics
cd "G:/Ai/RGB Control/_probe" && python solid_diag.py   # paint + read back every zone
cd "G:/Ai/RGB Control/_probe" && python hold_diag.py    # does the Corsair revert when we go silent?

# hardware diagnostics (round 10)
cd "G:/Ai/RGB Control" && python _probe/controller_location.py   # what `location` each controller reports
cd "G:/Ai/RGB Control" && python _probe/hid_mouse_probe.py       # HID collections / vendor pages
powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath 'G:\Ai\RGB Control\dist11\FullRGB.exe' -ArgumentList '--usbscan' -RedirectStandardOutput \$env:TEMP\usb.txt -PassThru -NoNewWindow; \$p.WaitForExit(60000)"
tr -d '\000' < "$LOCALAPPDATA/Temp/usb.txt"

# elevated engine task (RGB RAM). register prompts for UAC ONCE; status/run never do.
powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath 'G:\Ai\RGB Control\dist11\FullRGB.exe' -ArgumentList '--enginetask=status' -RedirectStandardOutput \$env:TEMP\et.txt -PassThru -NoNewWindow; \$p.WaitForExit(60000)"
tr -d '\000' < "$LOCALAPPDATA/Temp/et.txt"    # look for matchesThisInstall=True

# GUI verification (the app must already be running)
cd "G:/Ai/RGB Control/tools" && powershell.exe -NoProfile -ExecutionPolicy Bypass -File uiclick.ps1 Hardware
cd "G:/Ai/RGB Control/tools" && powershell.exe -NoProfile -ExecutionPolicy Bypass -File grab.ps1 shot FullRGB

# publish (use a FRESH dist dir if an elevated build is running)
cd "G:/Ai/RGB Control/src/FullRGB" && dotnet publish -c Release -r win-x64 -p:PublishDir=G:/Ai/RGB\ Control/dist11/ --nologo -v q

# engine logs (device detection, PawnIO/SMBus results)
ls -t "$APPDATA/OpenRGB/logs/" | head -3
```

Notes for the next agent:
- An elevated `FullRGB.exe` (or the `OpenRGB.exe` it started) **cannot be killed** by the unelevated
  agent shell. Use `powershell -Verb RunAs` or ask the user.
- Test output is UTF-16LE; pipe through `tr -d '\000'` before grepping.
- `bash -c 'cd X && ...'` with a long inline heredoc gets BLOCKED by the agent's command parser.
  Write the script to `$LOCALAPPDATA/Temp/x.py` with write_file, then run `python .../x.py`.
- L10n edits must touch BOTH dictionaries (en first, fa second) or `--uitest` fails on
  `l10n: en and fa cover the same keys`. Anchor on a nearby key and use `str.index` twice.

---

## 9. Remaining work (by value)

0. **Round-14 audit — the two P1s are FIXED (2026-09-09 13:0x), P2/P3 remain**, details in `AUDIT-2026-09-09.md`:
   - **P1 (FIXED)** hash identity is now `EngineBundle.CurrentHash()`; vendor fallback never recorded.
   - **P1 (FIXED)** splash-close during UAC: OCE caught in the `Loaded` lambda; repair skips the restart when the window is gone.
   - **P2 (open)** `TaskStartError` has zero consumers — surface the stale-task state in the GUI or delete the property.
   - **P2 (open)** declined repair = engine runs unelevated with no hint until next bundle change — tray/status hint driven by `TaskIsStale`.
   - **P2 (open)** `verify.sh` fxtest leg captured no output on 09-09 morning (manual run fine) — re-verify with the GUI closed.
   - **P3 (open)** untrack `_probe/st_out.txt` (+ the other `_probe` output files).
   - Round-14 FEATURES (audio decay, palette blocks, Ambient, Gaming) are DONE and verified — see §10.
1. **User GUI smoke test of `dist22\FullRGB.exe`** — exit any older FullRGB from the tray first, then:
   the four pages switch; the hero preview animates; a solid colour looks the same everywhere;
   the music effect is dark in silence and reacts to bass when set to Bass; changing the accent
   recolours the whole window; per-zone override (pump ring ≠ fans) applies; closing the window
   parks the app in the tray. On the Hardware page: the four controlled devices and the two
   unsupported peripherals are listed, and "Open engine log folder" opens `%APPDATA%\OpenRGB\logs`.
   **New in this fix:** on Lighting, `Rainbow`/`ColorCycle`/`Fire`/`Temperature`/`Custom` no longer
   show a misleading "Color #00E5FF" row — verified by `--rendertest` `colorRow` asserts.
2. **Prune `dist`…`dist9`** once the smoke test passes — `dist11` is the only one that matters (keep
   `dist10` until confirmed) and it
   is a single file. NOTE: the `FullRGB-Engine` task points INTO `%LOCALAPPDATA%\FullRGB\engine\...`
   now, not into a dist folder, so deleting old dist folders cannot break it.
3. **Per-zone calibration** — calibration is per device; the pump ring and the fans are different
   chips on the SAME device, so per-zone gain is the honest fix for the last colour mismatch.
4. **GPU support** — either wait for an OpenRGB detector for the Zotac 4070 Ti SUPER or add a native
   NvAPI I2C path.
5. **Mouse/keyboard lighting (`30FA:1140`, `2A7A:939F`)** — only reachable by writing an OpenRGB
   device driver for those PIDs. Requires capturing the vendor tool's USB traffic first
   (Wireshark + USBPcap). Do NOT brute-force SET_FEATURE payloads: bricking risk, see §3b.
6. **Native ASUS Aura driver** (phase-2 roadmap) — lower latency than routing through OpenRGB.
7. **Cleanup** — gate the `fullrgb-sdk.log` trace behind `--debug`, delete `_probe\icontest\`, and
   manually delete `src\FullRGB\binReleasenet8.0-windowswin-x64publish2 --nologo\`
   (agent sandbox blocked it).

---

## 10. Round 14 (2026-09-09) — audio decay, honest palette, screen-driven effects

**User reports driving this round:**
1. "رو حالت موزیک بعد قطع صدا 1-2 ثانیه طول میکش نور خاموش بشه" — the lights kept glowing 1–2 s after the music stopped.
2. "حالت گیمینگ هم بزار" — a gaming mode.
3. "نور ها با رنگ های صفحه نمایش یکی باش" — screen ambient lighting.
4. "من چند رنگ انتخاب میکنم ولی نور یک رنگ ترکیبی نشون میده" — picked colours blended into one mixed hue instead of showing separately.
5. Follow-up with screenshot: Music/Royal (#FF4D4D + #3D5AFE), Bass, Pulse, Gradient — "حتی ی چشمک ساده هم نمیزنه، نور ثابته صورتی" (no reaction to sound at all; constant pink instead of the picked red/blue).

**Root causes found for #5 (verified by `_probe/AudioProbe` — the provider itself is
HEALTHY: a 220 Hz tone drives level=1.0/bass=1.0 and silence decays to 0 in ~0.6 s):**
- **The "pink" was BY DESIGN, wrongly:** `AudioMode=pulse` sampled the colouring at ONE
  fixed point (`pos 0.5`) — the exact midpoint of the red↔blue gradient, i.e. frozen pink
  for the whole strip. Pulse now TRAVELS the colouring (sin period ~2.4 s), so both picked
  colours alternate visibly and the beat intensity still breathes the brightness.
- **`palette` with an empty/invalid CustomPixels silently fell through to the 2-colour
  gradient** (same pink blend). It now falls back to the red/green/blue default blocks.
- Screen sampling moved OFF the WASAPI callback thread onto its own `System.Threading.Timer`
  (a GDI grab inside the audio callback both delays analysis and freezes ambient colour on
  silence — no callbacks fire when nothing plays).

**Fixes / features (all verified on the real rig):**

1. **Silence decay 5× faster.** The envelope release in `AudioProvider` (level AND the three
   bands) was `0.15`/analysis-frame ≈ 600 ms to reach the 2 % noise gate; on top of the
   meter's smoothing the glow lingered 1–2 s. Release is now `0.45` → below the gate in
   ~5 frames (~120 ms). Attack path unchanged (beats still punch instantly).
   `--rendertest` §39 asserts decay ≤ 8 frames.
2. **Palette = discrete BLOCKS, not a blended ramp.** `EffectType.Custom` and the music
   effect's `AudioColor="palette"` now divide the strip into one solid segment per picked
   colour (`SampleBlocks`) — red+green+blue shows a red block, a green block, a blue block.
   Gradient blending still exists for the *gradient* colouring; the palette mode's whole
   point is "show every colour I picked separately".
   `--rendertest` §39 asserts a 90-LED strip at Time=0 maps picks 1:1 to thirds.
3. **NEW `Ambient` effect (16).** The screen is sampled into three horizontal bands
   (top/middle/bottom) every 80 ms on its own timer thread (`PollScreenColour`,
   GDI `CopyFromScreen` + `DrawImage` downscale, EMA-smoothed, no elevation); each ZONE
   paints its band's colour, so multiple strips mirror their part of the display. A single
   strip shows a vertical gradient across the three bands. No sample yet → dimmed primary.
4. **NEW `Gaming` effect (17).** Screen-average colour paints the strip; `Beat` flashes it
   white (`BeatStrength`). No sample yet → primary colour. Tray → **"Gaming lights"**
   (`ApplyGamingEverywhere`) applies it as per-device overrides for this session without
   rewriting the profile; the next profile switch restores the profile.
5. **Round-14 audit P1 fixes are IN:** `RepairStaleEngineTaskAsync` now compares against
   `EngineBundle.CurrentHash()` (the real bundle SHA, never the vendor folder name), and the
   splash's async-void `Loaded` catches OCE (closing during the UAC prompt no longer crashes;
   the post-repair engine restart is skipped when the window is gone). P2s remain open.

**Audio chain proven end-to-end by `_probe/AudioProbe.csproj`** (standalone host of the REAL
`AudioProvider` + `tone_test.py` WAV): tone → level 1.0 within ~0.6 s, silence → 0.0 in
~0.6 s, `screenSamples=85` in 12 s. If the live app still shows no reaction, the renderer
settings (AudioBand/AudioGain) or the running exe build are the suspects, not the provider.

Artifacts: `dist23\FullRGB.exe` (87.3 MB, 14:03 — dist22 is LOCKED by the running old
instance, per PLAN §7). Gates: rendertest ALL PASSED, uitest ALL PASSED, fxtest dist23
devices=4 framesSent=724 errors=0.

## 10a. Round 13 (2026-09-06) — v1.2.0: never require Task Manager again

**User report:** "OpenRGB wouldn't close; I killed it from Task Manager and everything worked."
Diagnosis: a wedged engine holds the SDK port but never answers; `StartAsync` attached on
port-open alone, so the GUI painted into a corpse with zero errors.

Fixes (all verified on the real rig):
1. `SdkAliveAsync` — real `REQUEST_CONTROLLER_COUNT` probe before attaching. A corpse is
   force-killed and replaced with a fresh engine during `StartAsync`.
2. `EngineTask.EndTaskInstance` — `schtasks /End` retires the elevated task engine with NO UAC
   (verified live). `StopIncludingElevated` tries it before the UAC-prompting script.
3. MainWindow watchdog — once a minute, probe the engine; two consecutive failures trigger the
   existing `ReviveEngine` path (silent mid-session wedge was previously undetectable: writes
   into a half-dead TCP still "succeed").
4. `CloseEngineOnExit` default **true** — closing the app no longer leaves `OpenRGB.exe`
   lingering to go stale. Existing settings.json keeps its old value; set it in Settings.

E2E test: froze a real engine (`NtSuspendProcess`, rc=0, probe TIMEOUT), launched dist20 →
old engine killed, new engine up, SDK answered, GUI painting again.

Artifacts: `dist20\FullRGB.exe` (v1.2.0, 83.2 MB single file).