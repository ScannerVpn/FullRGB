---
layout: default
title: Diagnostics
---

# Diagnostics

## The diagnostics zip

`Hardware → Export diagnostics` (next to "Check engine update") writes a zip with:

| File | Contents |
|---|---|
| `system.txt` | app + engine versions and paths, OS, elevation, engine task state, PawnIO state |
| `controllers.txt` | every engine controller: vendor, type, LED count, zones and their sizes |
| `support-matrix.txt` | the Hardware page as text: per device — state, VID:PID and the one-line reason |
| `app-log.txt` | FullRGB's own rolling log (recoveries, control-bus commands, update steps) |
| `engine-log.txt` | the tail of the latest OpenRGB log (detection, PawnIO/SMBus results) |
| `settings.json` | your current settings (profiles included; no secrets — the API token is NOT stored there) |
| `report.txt` | the same text "Copy report" puts on your clipboard |

Headless (for scripts/CI): `FullRGB.exe --export-diagnostics[=path.zip]`.

Attach this zip to any hardware bug report — it answers the questions support would otherwise
ask one by one.

## The app log

Since 1.6 the app keeps its own log at `%APPDATA%\FullRGB\logs\app-YYYYMMDD.log`
(14-day retention, 600-line in-memory ring). The engine log (OpenRGB's own) is in
`%APPDATA%\OpenRGB\logs\` — the Hardware page links straight to it.

## What is NOT in the zip

- The **API token** — it lives only in memory and `%APPDATA%\FullRGB\api-token`.
- Anything from other users on the machine.
- USB traffic captures — if you want to help write a community protocol, capture your own
  traffic (USBPcap) and share it deliberately.
