# FullRGB — PLAN.md

**Last updated:** 2026-09-06 (round 13: auto-replace hung engine, no-UAC engine stop, watchdog — v1.2.0)
**Repo root:** `G:\Ai\RGB Control` (no git)
**Status:** WORKING and verified on the real rig.

| Gate | Command | Latest result |
|---|---|---|
| Logic | `FullRGB.exe --rendertest` | **ALL RENDER TESTS PASSED** (81 asserts incl. 12× `colorRow`) |
| UI (XAML/resources/glyphs/l10n/bundle/USB) | `FullRGB.exe --uitest` | **ALL UI TESTS PASSED** |
| Real hardware | `FullRGB.exe --fxtest --seconds=15` | **devices=4, framesSent=1684, errors=0** |
| Engine task | `FullRGB.exe --enginetask=status` | `registered=True matchesThisInstall=True pawnio=True elevated=False` |
| USB inventory | `FullRGB.exe --usbscan` | 9 devices; mouse + keyboard identified by product string |
| Screenshots | `FullRGB.exe --uishot` | 6 PNGs in `%TEMP%\fullrgb-shots` |

**Latest build: `dist16\FullRGB.exe`** (v1.1.0, 83.2 MB single file) + GitHub release
<https://github.com/ScannerVpn/FullRGB/releases/tag/v1.1.0> (CI-built `FullRGB.exe` + `.sha256`).
Round-12 features: Spectrum/Scanner/Sparkle/Plasma effects, music shapes (bar/mirror/pulse/dots)
+ colourings (gradient/palette/level/rainbow), peak-hold, background colour, sensitivity + beat-flash,
12 presets, palette + extra-colour editors, rotation scheduler, per-app profiles, per-zone calibration,
settings backup/export/import, engine version + hardware report + upstream update check.
CI lesson: redirected stdout encoding differs per runner (UTF-16LE vs UTF-8) — the workflow
sniffs NUL density instead of assuming; the FAIL veto anchors on `^[FAIL]`/`TEST(S) FAILED`
because test names contain "failure". Old `dist12`–`dist14` pruned; `dist15` kept as fallback.

This document is self-contained: an agent can continue from it without reading the codebase first.

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

1. **User GUI smoke test of `dist11\FullRGB.exe`** — exit any older FullRGB from the tray first, then:
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

## 10. Round 13 (2026-09-06) — v1.2.0: never require Task Manager again

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
