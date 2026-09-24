# FullRGB v1.3.0 — راند ۱۴

## جدید در این نسخه

### 🎵 واکنش سریع‌تر به صدا
- بعد از قطع صدا، نورها حالا در **حدود ۰٫۱ ثانیه** خاموش می‌شوند (قبلاً ۱–۲ ثانیه طول می‌کشید).
- حالت Pulse (تنفسی) دیگر روی یک رنگ ثابت گیر نمی‌کند — رنگ روی گرادیان حرکت می‌کند و با بیس نفس می‌کشد.

### 🎨 رنگ‌های پالت، جدا و مشخص
- رنگ‌هایی که انتخاب می‌کنی به‌صورت **بلوک‌های مجزا** روی نورها نشان داده می‌شوند: قرمز → بلوک قرمز، آبی → بلوک آبی (دیگر با هم مخلوط نمی‌شوند).
- در حالت Palette اگر پالت خالی باشد، به‌جای ترکیب‌شدن، بلوک‌های پیش‌فرض قرمز/سبز/آبی نشان داده می‌شود.

### 🖥️ افکت جدید: محیطی (Ambient — Screen)
- نورها رنگ **صفحه‌نمایش** را دنبال می‌کنند: صفحه به سه نوار افقی تقسیم می‌شود و هر ناحیه از نور، رنگ همان بخش از تصویر را می‌گیرد.
- بدون نیاز به ادمین؛ نرم و بدون سوسو (میانگین‌گیری هوشمند).

### 🎮 افکت جدید: گیمینگ
- میانگین رنگ صفحه کل نورها را رنگ می‌کند و روی ضربه‌ی بیس، **فلش سفید** می‌زند.
- دسترسی سریع از **Tray → چراغ‌های گیمینگ** — بدون باز کردن برنامه، کل سیستم یک‌جا روی حالت گیمینگ می‌رود و با سوییچ پروفایل برمی‌گردد.

### 🔧 پایداری
- بستن پنجره‌ی شروع در وسط پنجره‌ی UAC دیگر برنامه را نمی‌بندد.
- تعمیر خودکار task موتور (نور رم) بعد از آپدیت برنامه، با هش صحیح باندل — رم دیگر بی‌سروصدا حذف نمی‌شود.

## تست‌ها
- `--rendertest` و `--uitest`: همه پاس
- `--fxtest` روی سخت‌افزار واقعی (ASUS Z790 + Corsair Commander Core + 2× ENE DRAM): ۴ دستگاه، ۷۲۴ فریم، صفر خطا

## دانلود
`FullRGB.exe` (تک‌فایل، ~۸۴ مگ) را اجرا کن — بدون نصب، بدون ادمین.
اگر رم RGB داری: `سخت‌افزار → فعال‌سازی نور رم` (فقط یک بار UAC).

---

# FullRGB v1.3.0 — round 14

## What's new

### 🎵 Faster audio response
- Lights now drop to black in **~0.1 s** after the music stops (was 1–2 s).
- Pulse style travels the colouring instead of freezing on the gradient midpoint.

### 🎨 Palette colours, separate and visible
- Picked colours render as **discrete blocks**: red → a red block, blue → a blue block, no blending.
- An empty palette falls back to default RGB blocks instead of melting into a two-colour mix.

### 🖥️ New effect: Ambient (Screen)
- Lights mirror the **display**: the screen is sampled into three horizontal bands and each zone paints its band's colour. No admin needed; smoothed so it never strobes.

### 🎮 New effect: Gaming
- The screen's average colour paints the strip with a **white flash** on bass hits.
- Quick access from **Tray → Gaming lights** — one click applies it rig-wide without touching the profile.

### 🔧 Stability
- Closing the splash during the UAC prompt no longer crashes the app.
- The engine-task self-repair now uses the correct bundle hash, so RGB RAM survives app updates.

## Verification
- `--rendertest` / `--uitest`: all passed
- `--fxtest` on the reference rig (ASUS Z790 + Corsair Commander Core + 2× ENE DRAM): 4 devices, 724 frames, 0 errors

SHA-256 of `FullRGB.exe`: `1E002E504F175E890F3593B12FEF529AD72B53023D82312EC9391342144829F0` (also in `FullRGB.exe.sha256`).
