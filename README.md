# FullRGB

**System-wide RGB control for Windows — one app, one file, no plugins.**

[![windows-build](https://github.com/ScannerVpn/FullRGB/actions/workflows/windows-build.yml/badge.svg)](https://github.com/ScannerVpn/FullRGB/actions/workflows/windows-build.yml)
[![release](https://img.shields.io/github/v/release/ScannerVpn/FullRGB?include_prereleases)](https://github.com/ScannerVpn/FullRGB/releases/latest)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[فارسی](#فارسی) · [English](#english)

---

## English

### What is it?
WPF (.NET 8, single-file `FullRGB.exe`) that drives **every RGB device** detected on the PC at once — motherboard headers, RAM, coolers, fans, LED strips — with software effects, without asking the user to install plugins.

- **Single file:** `FullRGB.exe` ~84 MB, self-contained. The OpenRGB engine is **embedded inside the exe** (`FullRGB.engine.zip` 13 MB) and unpacked once to `%LOCALAPPDATA%\FullRGB\engine\<hash>\`. No `vendor\` folder to ship.
- **Bilingual:** English / فارسی (RTL).
- **Compact UI:** 4 tabs — Lighting / Devices / Hardware / Settings — with live hero preview (same `EffectRenderer` as hardware).
- **Tray + autostart:** closing the window parks to tray; autostart via Scheduled Task `/RL LIMITED` (no admin).
- **Profiles + per-zone/per-device overrides.**

### Features
- 12 effects: Solid, Gradient, Rainbow, ColorCycle, Breathing, Wave, Comet, Blink, Fire, Temperature (CPU/GPU), AudioVU (bass/mid/treble/level), Custom
- Real FFT audio pipeline with noise gate (`level<=0.02 → black`)
- Per-device colour calibration (R/G/B gain + gamma) — fixes “same colour looks different on pump vs fans”
- Per-zone LED count (resizable Addressable zones)
- Hardware audit page: Controlled / Needs elevation / Unsupported / Unknown — with VID:PID and **why** a device is missing
- Engine diagnostics: elevated vs unelevated, SMBus/PawnIO status, protocol version, fps (rendered vs delivered)

### Supported hardware — does it detect *any* brand?
**Per-model, not per-brand.** FullRGB bundles OpenRGB (≈2361 detectors). Broadly covered: ASUS / MSI / Gigabyte / ASRock motherboards, Corsair Commander Core/Pro, G.Skill/Kingston/Corsair RAM, Razer/Logitech/SteelSeries mainstream, hubs. 

- **RGB RAM / some GPUs need one extra step:** `Hardware → Unlock more hardware` registers the engine as an elevated on-demand Scheduled Task (`FullRGB-Engine`, `RunLevel=Highest`). One UAC prompt ever; FullRGB itself stays `asInvoker`. Without it only 2 devices appear, with it 4 (e.g. 2× ENE DRAM + board + cooler = 730 LEDs on the test rig).
- **Cheap OEM mouse/keyboard** (e.g. CASUE `2A7A:939F`, INSTANT `30FA:1140`) usually has **no driver** — each vendor uses its own undocumented HID reports. They appear as “Detected, not controllable” with VID:PID — safe, just lighting stays on firmware. Writing a guessed protocol risks bricking, so FullRGB probes read-only.

### Install (for testers)
1. Download `FullRGB.exe` from the latest Release (below).
2. Run it — no installer, no admin required.
3. If you have RGB RAM, go to `Hardware → Unlock more hardware → Enable RGB RAM` (one UAC). Rescan if needed.
4. `Settings → Start with Windows` if you want autostart. New: `Close lighting engine when FullRGB exits` — if on, the bundled OpenRGB is killed on Exit (one UAC when elevated); if off, lights stay as they were.

### Build from source
```powershell
git clone https://github.com/ScannerVpn/FullRGB.git
cd FullRGB
# vendor/OpenRGB/OpenRGB Windows 64-bit/ must be present (58 MB, already in repo)
dotnet build -c Debug
bash tools/verify.sh Debug          # rendertest + uitest + fxtest
dotnet publish -c Release -r win-x64 -p:PublishDir=dist\ --nologo -v q
.\dist\FullRGB.exe                  # or --rendertest / --uitest / --uishot / --fxtest
```

### Project layout
```
FullRGB/
├─ PLAN.md                     ← handoff doc: read this first
├─ .github/workflows/          ← windows-build.yml (CI: gates + single-file publish)
├─ src/FullRGB/                ← WPF app (net8.0-windows)
│  ├─ SDK/EngineBundle.cs      ← embed/extract engine
│  ├─ SDK/OpenRgbProcessManager.cs
│  ├─ Setup/EngineTask.cs      ← elevated task for SMBus
│  ├─ Diag/UsbScan.cs + SupportMatrix.cs
│  └─ Effects/EffectEngine.cs
├─ vendor/OpenRGB/...          ← engine source tree (zipped at build)
└─ tools/verify.sh, grab.ps1, uiclick.ps1, anim.ps1
```

### Releases
Grab `FullRGB.exe` from [Releases](https://github.com/ScannerVpn/FullRGB/releases/latest).
Builds are produced by CI (`windows-build.yml`) which runs `--rendertest` + `--uitest` and
verifies the embedded engine is present before attaching the exe.

---

## فارسی

### این برنامه چیست؟
اپ WPF (.NET 8, تک‌فایل `FullRGB.exe`) که همه قطعات RGB سیستم را یکجا کنترل می‌کند — هدرهای مادربرد، رم، کولر، فن، نوار LED — بدون نیاز به نصب پلاگین.

- **تک‌فایل:** حدود ۸۴ مگ، خودکفا. موتور OpenRGB داخل خودِ exe جاسازی شده و فقط یک‌بار در `%LOCALAPPDATA%\FullRGB\engine\...` باز می‌شود.
- **دو زبانه:** فارسی / انگلیسی، راست‌به‌چپ کامل.
- **رابط فشرده:** ۴ تب — نورپردازی / دستگاه‌ها / سخت‌افزار / تنظیمات

### نصب برای تست
۱. از بخش Releases فایل `FullRGB.exe` را دانلود کن
۲. اجرا کن — نیاز به نصب یا ادمین ندارد
۳. اگر رم RGB داری: `سخت‌افزار → فعال‌سازی سخت‌افزار بیشتر → فعال‌سازی نور رم` (فقط یک بار تایید ویندوز)
۴. `تنظیمات → بستن موتور نور هنگام خروج` — اگر روشن باشد موقع خروج از سینی، OpenRGB هم بسته می‌شود

### پوشش سخت‌افزار
برای هر **مدل** باید درایور نوشته شده باشد، نه هر برند. مادربردهای اصلی، رم‌ها، کنترلر Corsair پوشش خوبی دارند؛ موس/کیبوردهای ارزان OEM معمولا ندارند چون هرکدام پروتکل اختصاصی بدون مستند دارد.

---

## License
MIT — see `LICENSE` (OpenRGB engine inside is GPL-2.0, bundled as separate binary).
