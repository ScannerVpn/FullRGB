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
| CASUE USB KB | 2A7A:939F | ❌ not controllable | Vendor HID channel exists (COL04, usagePage `0xFF01`, 8-byte feature report) — protocol help welcome |
| INSTANT USB GAMING MOUSE | 30FA:1140 | ❌ not controllable | Same situation; probe read-only |
| Razer / Logitech / SteelSeries mainstream | — | ✅ works | Via bundled OpenRGB detectors |
| Cheap OEM mouse/keyboard (no driver) | — | 🧩 community candidate | Import a protocol file; see docs/hid-protocols |

## GPUs

| Device | VID:PID | Status | Notes |
|---|---|---|---|
| Zotac RTX 4070 Ti SUPER | 19DA:7675 | ❌ not controllable | OpenRGB registers the NvAPI I2C interface but has no controller for this board — not a FullRGB bug |

---

Hardware that is *present but grey* in the Hardware page has a one-line reason next to it —
that line is the same data this list is built from.
