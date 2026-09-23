# Compatibility list

> Community-maintained. **Support is per-model, not per-brand** — FullRGB bundles OpenRGB with
> ≈2361 detectors, so whether your device lights up depends on the exact model.

Legend:

| Mark | Meaning |
|---|---|
| ✅ works | Detected and controllable out of the box |
| 🔓 works elevated | Works after `Hardware → Unlock more hardware` (one UAC, for SMBus devices like RGB RAM) |
| 🧩 community | Works with an imported community protocol (see [docs/hid-protocols](docs/hid-protocols.md)) — usually read-only probe; writes are experimental |
| ❌ not controllable | Detected, but no driver exists; lighting stays on the device's firmware |
| ⬜ untested | Reported present, nobody confirmed yet |

## How to add your device

1. Open FullRGB → **Hardware → Export diagnostics** (or run `FullRGB.exe --export-diagnostics`)
   — it contains the exact VID:PID, controller list and support matrix.
2. Open a new thread in [Discussions → Show and tell / Compatibility](https://github.com/ScannerVpn/FullRGB/discussions)
   (or a PR editing this file) with:

   ```yaml
   device: "CASUE USB KB"
   vid_pid: "2A7A:939F"
   class: Keyboard
   fullrgb_version: "1.6.0"
   status: ❌ not controllable      # one of the legend marks
   notes: "Vendor HID channel present (usagePage 0xFF01); no public protocol doc."
   ```

3. If you got a device working with a community protocol file, link the JSON in the thread and
   mark it 🧩 with a note about whether you enabled experimental writes.

## Motherboards

| Board | VID:PID | Status | Notes |
|---|---|---|---|
| ASUS ROG Maximus Z790 Dark Hero | — | ✅ works | Board + headers via the engine |
| ASUS AURA LED Controller | 0B05:18F3 | ✅ works | HID interface |
| MSI / Gigabyte / ASRock (mainstream) | — | ⬜ untested | Engine has detectors; report your board |

## Memory (RGB RAM)

| Device | Status | Notes |
|---|---|---|
| ENE DRAM (G.Skill / Kingston / Corsair DDR5) | 🔓 works elevated | Needs the elevated engine task; DDR5 IMC path (SkylakeIMC PawnIO module) is expected to fail harmlessly |

## Coolers / controllers / fans

| Device | Status | Notes |
|---|---|---|
| Corsair Commander Core | ✅ works | Per-zone writes ~19.7 fps; iCUE must be closed (FullRGB offers a one-click close) |
| Corsair Commander Pro | ✅ works | |
| Motherboard ARGB headers (ASUS/MSI/Gigabyte) | ✅ works | Addressable zones default to max LED count — set the real count on Devices |

## Peripherals (mice / keyboards)

| Device | VID:PID | Status | Notes |
|---|---|---|---|
| CASUE USB KB | 2A7A:939F | ❌ not controllable | **No vendor HID channel at all.** Its three collections are all standard (`0x0001/0x0006` keyboard, `0x000C/0x0001` consumer, `0x0001/0x0080` system control) and none exposes a feature report — a community protocol file cannot reach this device |
| INSTANT USB GAMING MOUSE | 30FA:1140 | 🧩 community candidate | **This is the one with the vendor channel:** `usagePage 0xFF01 / usage 0x0001` with an 8-byte feature report (plus `0xFF00/0x0001`). A protocol file has something to talk to here — probe is read-only and safe |
| Razer / Logitech / SteelSeries mainstream | — | ✅ works | Via bundled OpenRGB detectors |
| Cheap OEM mouse/keyboard (no driver) | — | 🧩 community candidate | Import a protocol file; see docs/hid-protocols. Check the collection list first: without a vendor `usagePage` (0xFF00–0xFFFF) and a feature report, there is nothing to write to |

> Collection lists above were measured with `HidP_GetCaps`, not copied from a spec sheet.
> The 0xFF01 channel was previously listed against the keyboard; that was wrong — it belongs
> to the mouse.

## GPUs

| Device | VID:PID | Status | Notes |
|---|---|---|---|
| Zotac RTX 4070 Ti SUPER | 19DA:7675 | ❌ not controllable | OpenRGB registers the NvAPI I2C interface but has no controller for this board — not a FullRGB bug |

## Community protocol writes — risk and tested devices

Reading a community protocol (a probe) is always safe: it is the same read-only feature-report
traffic FullRGB already uses for detection. **Writing is not.** A protocol file is an untested
guess about a firmware nobody has documentation for, and a wrong `SET_FEATURE` payload can leave
a device's lighting wedged until it is unplugged or power-cycled.

FullRGB therefore gates every write on all of: the file declaring `"experimentalWrite": true`, the
global switch in **Hardware → Community protocols**, and **two confirmations on every single test
paint** — a yes/no dialog, then the device's VID:PID typed out by hand.

This table records what the community has actually tried. Add a row when you test one — the
failures are the most useful rows here.

| Device | VID:PID | Protocol file | Result | Notes |
|---|---|---|---|---|
| CASUE USB KB | 2A7A:939F | — | 🚫 not reachable | No vendor HID collection and no feature report on any of its three collections — nothing to probe or write |
| INSTANT USB GAMING MOUSE | 30FA:1140 | — | ⬜ probe only | Vendor channel present (`usagePage 0xFF01 / usage 0x0001`, 8-byte feature report); read tested, no write protocol published yet |

Result legend: `✅ lit up` — write worked and the device stayed responsive · `⚠️ partial` — some
modes/colours worked · `❌ no effect` — write was accepted but nothing changed · `🧱 bricked` —
device needed a replug/power cycle afterwards · `⬜ probe only` — read tested, write not attempted ·
`🚫 not reachable` — the device exposes no vendor collection/feature report, so no protocol file
can address it.

---

Hardware that is *present but grey* in the Hardware page has a one-line reason next to it —
that line is the same data this list is built from.
