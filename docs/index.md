---
layout: home
title: FullRGB
permalink: /
---

# FullRGB documentation

**System-wide RGB control for Windows — one app, one file, no plugins.**
FullRGB is a single-file WPF app (.NET 8) that drives every RGB device OpenRGB can detect —
motherboard headers, RAM, coolers, fans, strips — with software effects. The engine is
embedded inside the exe; nothing to install, and the app itself never asks for admin.

[Download the latest release](https://github.com/ScannerVpn/FullRGB/releases/latest) ·
[Source](https://github.com/ScannerVpn/FullRGB) ·
[Compatibility list](https://github.com/ScannerVpn/FullRGB/blob/main/COMPATIBILITY.md)

## Guides

| Guide | What it covers |
|---|---|
| [Automation (CLI, pipe, HTTP API)](automation.md) | Drive a running FullRGB from scripts, AHK, PowerShell, or anything that can talk HTTP — including game events for the GamePulse effect |
| [Scheduling & per-app profiles](scheduling.md) | Time-of-day profiles (`22:00-07:00=Night`), app-triggered profiles, the GamePulse effect |
| [Community HID protocols](hid-protocols.md) | Light up driverless OEM mice/keyboards with shared protocol files — read-only probes, guarded writes |
| [Troubleshooting](troubleshooting.md) | Why RGB RAM needs one UAC, why a device is "detected but not controllable", sleep/wake behaviour, iCUE conflicts |
| [Diagnostics](diagnostics.md) | The bug-report zip, the app log, and what support needs from you |

## Quick facts

- **Single file:** `FullRGB.exe` (~84 MB, self-contained). The OpenRGB engine is embedded and
  unpacked once to `%LOCALAPPDATA%\FullRGB\engine\<hash>\`.
- **Bilingual:** English / فارسی (full RTL).
- **Profiles:** global effect + per-device/per-zone overrides, per-zone LED counts, colour
  calibration, import/export with shareable codes (`FRGB1-…`).
- **Automation:** CLI one-shots, named pipe `\\.\pipe\fullrgb-ctrl`, HTTP API on
  `127.0.0.1:9372`, mobile companion page (PIN + token on the LAN).
- **Self-healing:** frame watchdog, wake-up rebuild, engine safe mode with backoff.
- **Updates:** checked daily from GitHub Releases; downloaded, verified (SHA-256), and swapped
  in atomically at the next start.

## Support

Bug reports: attach the diagnostics zip (Hardware → **Export diagnostics**), then open an
issue with the [device-report template](https://github.com/ScannerVpn/FullRGB/issues/new?template=device-report.yml)
for hardware problems. Contributions are welcome — start with
[CONTRIBUTING.md](https://github.com/ScannerVpn/FullRGB/blob/main/CONTRIBUTING.md).
