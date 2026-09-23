# Contributing to FullRGB

Thanks for your interest in making FullRGB better! This document covers the practical stuff:
how the codebase is laid out, how changes are gated, and what a good PR looks like.

## Project overview

FullRGB is a single-file Windows WPF app (.NET 8) that drives RGB hardware through an embedded
OpenRGB engine. The important constraint that shapes almost every decision: **the shipped
artifact is ONE `FullRGB.exe` (~84 MB) with the engine zipped inside it, and the app never
self-elevates.** Before proposing a change, check that it survives those two rules.

## Getting started

```powershell
git clone https://github.com/ScannerVpn/FullRGB.git
cd FullRGB
# vendor/OpenRGB/OpenRGB Windows 64-bit/ is already in the repo (needed at build time)
dotnet build -c Debug
bash tools/verify.sh Debug        # rendertest + uitest + fxtest (fxtest needs RGB hardware)
```

You need the .NET 8 SDK and Windows to build (WPF does not build on Linux/macOS).

## The gates

Every PR must pass the headless gates on CI (they run automatically):

| Gate | Command | What it protects |
|---|---|---|
| Logic | `FullRGB.exe --rendertest` | Effect rendering consistency, schedule rules, profile share, dispatcher, HID validation, updater compare |
| UI | `FullRGB.exe --uitest` | Every window/dialog instantiates, resources exist, glyphs exist, l10n is complete (en ⇄ fa) |
| Screens | `FullRGB.exe --uishot` | (Optional) PNGs of all pages for layout review |
| Real HW | `FullRGB.exe --fxtest --seconds=14` | Frames actually reach hardware — run locally if you have a rig |

The UI test checks that **every localization key exists in both English and Persian**
(`L10n.MissingKeys()`) and that **every MDL2 codepoint you reference actually exists in the
font** (`GlyphCheck`). If you add UI, add the strings and re-check glyphs — the gate will
catch it either way, but faster for you to run it first.

## Code layout (read PLAN.md for the full map)

```
src/FullRGB/
├─ App.xaml(.cs)          startup, headless gates, CLI one-shots, update swap
├─ MainWindow.*.cs        shell + pages (lifecycle / devices / effects / hardware / settings / automation)
├─ Automation/            control bus: dispatcher, named pipe + HTTP hub, IControlTarget
├─ Companion/             embedded mobile companion page
├─ Update/                GitHub Releases updater (check / download / atomic swap)
├─ Hid/                   community protocol model + minimal Win32 HID bridge
├─ Diag/                  USB scan, support matrix, app log, diagnostics export
├─ Setup/                 engine task, watchdog, safe mode
├─ SDK/                   OpenRGB protocol client + process manager
├─ Effects/               effect catalog, renderer, engine loop
├─ Config/                profiles, settings, schedule rules, profile share
├─ Sensors/               temps, audio, game events
└─ L10n.cs                en/fa strings — every user-visible string lives here
```

## Ground rules

1. **Append-only enums.** `EffectType` is serialized as numbers in user settings. New effects
   are appended at the end, never inserted. Same for anything that lands in settings.json.
2. **Bilingual strings.** Every user-visible string goes into `L10n.cs` in BOTH `en` and `fa`.
3. **No self-elevation.** The app stays `asInvoker`. Anything that needs admin goes through
   the on-demand elevated engine task pattern (see `Setup/EngineTask.cs`).
4. **Hardware writes need gates.** If your change writes to HID devices, it must respect the
   community-protocol safety model in `Hid/CommunityProtocols.cs` — read-only by default,
   write only behind every declared gate. "It worked on my mouse" is not a gate.
5. **Fail-soft.** Settings, logs, and optional sensors are best-effort; a throw in a default
   path that darkens someone's rig is a bug. Look at how existing code wraps optional paths.
6. **Honest UI.** Status lines never show placeholder values ("—", "0 fps" when offline).
   Say what is known, say why when something is missing (that is what the Hardware page is).
7. **Test what is pure.** Anything that can be asserted without hardware belongs in
   `RenderTests.cs` (logic) or `UiTests.cs` (windows/resources). CI runs those; use them.

## Commit & PR style

- Small, focused PRs. One feature or one fix per PR.
- Follow the existing comment style: comments explain WHY (they are full of measured facts and
  past bugs), not what. If a change fixes a real user-visible bug, leave a comment so the next
  person does not "simplify" it back into the bug.
- Update `CHANGELOG.md` under an "Unreleased" heading.
- If your change touches behaviour users can see, say so in the PR body with before/after.

## Reporting bugs

Hardware problems: attach the diagnostics zip (Hardware → Export diagnostics, or
`FullRGB.exe --export-diagnostics`) and check COMPATIBILITY.md for whether your device is
already known. Use the device-report template for "my keyboard is detected but not controllable"
reports.

## Feature requests

Open a Discussion first for anything that adds a new surface (new effect, new automation
transport, new page). The codebase is deliberately small; every feature must earn its place
and its keep in maintenance.

## License

By contributing you agree that your contributions are licensed under the MIT license that
covers the project. The bundled OpenRGB engine remains GPL-2.0 and is kept as a separate
binary — do not link engine code into the app.
