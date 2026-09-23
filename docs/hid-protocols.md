---
layout: default
title: Community HID protocols
---

# Community HID protocols (experimental)

A lot of cheap OEM mice and keyboards ship **without any driver or documented protocol**.
OpenRGB has no detector for them, so FullRGB lists them as *"Detected, not controllable"* —
safe, but the lighting stays on the device's firmware.

Since FullRGB 1.6 you can change that **with a shared protocol file**: a small, human-readable
JSON that names the HID report a specific model uses. Files are exchanged through issues,
discussions or chat, imported in `Hardware → Community protocols`, and matched to devices by
VID:PID.

## Safety model (read this first)

- **Reading is always allowed.** A probe sends one feature-report request and prints the
  answer. This is the same class of traffic detection already uses.
- **Writing is triple-gated.** All three must hold:
  1. the file declares `"experimentalWrite"` capability by including a `paint` section,
  2. you flip **Experimental writes: on** in the Hardware page (typed confirmation, stored),
  3. every single test paint asks twice, with a typed confirmation.
- **Bounded payloads.** Reports are 8–64 bytes; the paint frame is `report id + prefix +
  N × RGB + zero padding` from the file — no arbitrary byte injection.
- **Only the declared endpoint.** The app opens the HID collection the file names
  (usagePage/usage) for that exact VID:PID, nothing else.

Why so careful: writing a guessed SET_FEATURE payload at unknown firmware can leave the
lighting wedged (or, in the worst case, the device) until it is unplugged. The author of a
protocol file tested ONE unit; yours may differ. Read-only probing is how everyone starts.

## File format (`fullrgb.hid/1`)

```json
{
  "schema": "fullrgb.hid/1",
  "name": "CASUE USB KB",
  "author": "your-name",
  "vid": "2A7A",
  "pid": "939F",
  "notes": "Vendor channel COL04, usagePage 0xFF01. Probed with hid_mouse_probe.py.",
  "source": "captured with USBPcap / probed on 2026-09-23",
  "probe": { "usagePage": 65281, "usage": 1, "reportId": 1, "length": 8 },
  "paint": { "reportId": 6, "length": 64, "prefix": "06 01", "order": "RGB",
             "rgbSlots": 20 }
}
```

Field reference:

| Field | Meaning |
|---|---|
| `schema` | must be exactly `fullrgb.hid/1` |
| `name`, `author`, `notes`, `source` | shown in the UI; provenance matters, fill them in |
| `vid`, `pid` | exactly 4 hex digits, e.g. `2A7A` |
| `probe.usagePage`, `probe.usage` | the HID usage of the collection to open (decimal; `0xFF01` → `65281`) |
| `probe.reportId`, `probe.length` | feature report to read (report id 0 = none, length 2–64) |
| `paint.*` | omit the whole section for read-only protocols |
| `paint.reportId` | report id for SET_FEATURE (0 = none) |
| `paint.length` | total report bytes INCLUDING the report-id byte, 8–64 |
| `paint.prefix`, `paint.tail` | hex bytes written before / after the colour ("06 01") |
| `paint.order` | `RGB` or `BGR` |
| `paint.rgbSlots` | how many times the solid colour is repeated (1–20) |

Validation is strict on purpose: wrong schema, non-hex VID/PID, missing sections or a report
that would not fit are rejected at import **and** again at every load, so a hand-edited store
file cannot smuggle in what the UI would have refused.

## Workflow: writing a protocol for your device

1. Find your device's VID:PID on the Hardware page (or `FullRGB.exe --usbscan`).
2. Enumerate its HID collections: the repo's `_probe/hid_mouse_probe.py` and
   `_probe/mouse_feature_read.py` do exactly this (read-only, Python + hidapi).
3. Write a `probe` section first, import it, press **Probe (read)** and compare the bytes
   against what the vendor software writes (USBPcap helps).
4. Only when a report deterministically mirrors the lighting, add a `paint` section and test
   with **Test paint** — one solid colour, exactly one report.
5. Share the JSON in a Discussion; the compatibility list links accepted files.

Two example files live in [`docs/examples/`](examples/): a read-only probe and a paint
protocol, both annotated.

## Limitations

- Solid colours only. Animated streaming over a community protocol is not supported yet —
  many OEM devices only accept a slow polled mode anyway.
- No effect engine integration: the paint is a one-shot test, not a profile effect. Once a
  protocol is known to be safe across a few units, the better path is upstreaming a real
  OpenRGB detector — FullRGB then lights the device like any native one.
