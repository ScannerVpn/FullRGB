---
layout: default
title: Troubleshooting
---

# Troubleshooting

## Why does RGB RAM need "one UAC prompt"?

DRAM lighting sits on the SMBus (I2C) of your motherboard. Windows denies user-mode processes
access to SMBus; the kernel driver path needs **elevation of the ENGINE**, not of FullRGB.

- FullRGB itself always runs as a normal user (`asInvoker`).
- `Hardware → Unlock more hardware → Enable RGB RAM` registers the engine as a **Scheduled
  Task with RunLevel=Highest** (`FullRGB-Engine`). You approve one UAC prompt, ever.
- From then on the engine starts elevated through that task, no prompts; every HID device
  (board headers, Corsair controllers, ARGB fans) keeps working unelevated either way.
- Without it you will see 2 devices; with it 4 (2× ENE DRAM + board + cooler = 730 LEDs on
  the dev rig).
- `SmbusIntelSkylakeIMC.bin` always fails with `code=-2147024841` even elevated — the DDR5 IMC
  path is unsupported by that PawnIO module. DRAM is found over the i801 bus regardless, so
  the error is harmless noise.

After an engine update the task's absolute path changes (the engine extracts to a hash-keyed
folder); the app notices and offers a one-click re-register (one UAC again).

## Why is a device "detected, not controllable"?

Support is **per-model, not per-brand**. The bundled engine carries ≈2361 detectors; a device
without one shows up in Windows but has no lighting driver. The Hardware page groups devices
into *Controlled / Needs one setup step / Detected, not controllable* with the VID:PID and a
one-line reason — that reason is the honest answer, not a guess.

For cheap OEM mice/keyboards without any public protocol, community protocol files are the
path forward — see [hid-protocols](hid-protocols.md). For known-brand devices that should be
supported, attach the diagnostics zip and check the
[compatibility list](https://github.com/ScannerVpn/FullRGB/blob/main/COMPATIBILITY.md).

## Why does the lighting die after sleep / hibernate?

After a suspend the engine survives holding USB handles that died with it — it keeps
accepting SDK packets (so every "are you there?" probe says fine) while driving nothing.
FullRGB watches for this with **three independent triggers** (power broadcast + session
notifications, a heartbeat gap detector, and a once-a-minute side-watch of the engine's
frames) and rebuilds the session by itself: zones re-expanded, Direct mode re-asserted, a
fresh frame pushed to every zone. The `Settings → Fix the lighting automatically` switch
turns this off if you prefer manual control.

If the engine itself keeps dying (three full replacements in ten minutes), the app parks the
lights on a dim static colour, shows a **safe mode** banner and keeps retrying with a growing
delay — your profile comes back on its own once the engine holds.

## iCUE conflicts

iCUE and FullRGB fight over Corsair devices (both write to the same interfaces; zone resizes
and mode switches undo each other). FullRGB detects iCUE, shows a banner and offers a one-click
close (including the services that restart it). If you need iCUE for fan curves, exclude those
devices on the Lighting page instead.

## My effect looks wrong on long strips

Addressable zones default to their **maximum** LED count (120 per header, 34 per fan port).
Enter the real per-port counts on the Devices page so wave/comet spacing is physically
correct; `Identify` flashes one zone white so you know which port is which. If the same
colour looks different on a pump vs. a fan, use per-zone calibration (R/G/B gain + gamma).

## Nothing connects at all

1. `Hardware → Engine` card: is the engine running, via task or owned process?
2. Open the engine log folder from that card — detection failures are explained there.
3. Another OpenRGB instance (yours or another app's) may hold port 6742. FullRGB probes the
   port and kills a corpse it owns; an engine of a DIFFERENT install needs to be closed.
4. Still dark: `FullRGB.exe --export-diagnostics` and open an issue with the zip.

## GPU lighting

NVIDIA GPU lighting support in OpenRGB is partial; e.g. Zotac `19DA:7675` registers an I2C
interface but has no controller. If yours is unsupported, that is upstream, not a FullRGB
bug — the diagnostics zip shows exactly what the engine sees.

## The mouse2/keyboard with a community protocol stopped lighting

Unplug and replug the device (its firmware is in a vendor-defined state), then re-import the
protocol file after checking for an updated version in its Discussion thread. The app never
writes unless all three gates are on, so a stale file can not do this on its own.

## Updates

`Settings → Updates` shows the current version; checks run once a day against GitHub
Releases. Downloaded updates are SHA-256-verified before they are ever swapped in, and the
swap happens at the next start (or immediately, with your confirmation). Corporate proxies
that block api.github.com simply mean no checks — nothing else is affected.
