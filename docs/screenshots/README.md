# UI screenshots

Real UI captures are produced by the app itself:

```powershell
.\FullRGB.exe --uishot            # 4 PNGs at 1280×900 into %TEMP%\fullrgb-shots
.\FullRGB.exe --uishot --fa       # the RTL Persian pass
.\FullRGB.exe --uishot --out=DIR  # choose a folder
```

Expected files (drop them in this folder and they are picked up by the README):

| File | Page |
|---|---|
| `ui-lighting.png` | Lighting page with the live hero preview |
| `ui-devices.png` | Devices page (zone sizes, calibration) |
| `ui-hardware.png` | Hardware page (support groups + community protocols) |
| `ui-settings.png` | Settings page (profiles, schedule, companion, updates) |

`hero-banner.png` is a generated banner (see `tools/` history) used as the README's opener
until the real captures above are committed.
