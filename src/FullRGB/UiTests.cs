using System.Windows;
using Application = System.Windows.Application;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

namespace FullRGB;

/// <summary>
/// --uitest: loads every window and dialog WITHOUT hardware and without showing them, then
/// measures/arranges them. This catches XAML parse errors, missing StaticResource keys and
/// broken ControlTemplates — none of which the C# compiler sees, and all of which used to
/// only surface as a crash on the user's first launch.
/// </summary>
public static class UiTests
{
    public static int Run()
    {
        int failed = 0;

        void Check(string name, Action body)
        {
            try
            {
                body();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[FAIL] {name} — {e.GetType().Name}: {e.Message}");
                if (e.InnerException is not null)
                    Console.WriteLine($"        inner: {e.InnerException.Message}");
                failed++;
            }
        }

        // resource keys the code looks up by string: a typo here is a runtime crash
        foreach (var key in new[]
        {
            "Bg", "BgElevated", "Card", "CardHover", "Surface", "SurfaceHi", "Border", "BorderSoft",
            "Text", "Muted", "Faint", "Accent", "AccentDim", "AccentFill", "OnAccent", "Violet",
            "Pink", "Warn", "WarnBg", "Danger", "DangerBg", "Ok", "BrandGrad", "AppBackdrop",
            "CardGrad", "H1", "Txt", "SectionHdr", "MutedTxt", "FaintTxt", "CardBd", "Chip",
            "FxTile", "DeviceRow", "Btn", "IconBtn", "CaptionBtn", "CloseBtn", "AccentBtn",
            "GhostBtn", "Slider", "SliderThumb", "Inp", "Cmb", "CmbItem", "Chk", "Prog", "NavTab",
        })
        {
            var k = key;
            Check($"resource: {k}", () =>
            {
                if (Application.Current.TryFindResource(k) is null)
                    throw new KeyNotFoundException($"resource '{k}' is missing");
            });
        }

        Check("theme: accent applies to live brushes", () =>
        {
            Theme.ApplyAccent("#FF00AA");
            var b = (System.Windows.Media.SolidColorBrush)Application.Current.FindResource("Accent");
            if (b.Color.R != 0xFF || b.Color.B != 0xAA)
                throw new InvalidOperationException($"accent brush not updated: {b.Color}");
            Theme.ApplyAccent("#00E5FF");
        });

        Check("MainWindow: XAML loads and lays out", () =>
        {
            var w = new MainWindow();
            w.Measure(new Size(560, 700));
            w.Arrange(new Rect(0, 0, 560, 700));
            w.Close();
        });

        Check("StartupWindow: XAML loads and lays out", () =>
        {
            var w = new StartupWindow();
            w.Measure(new Size(460, 392));
            w.Arrange(new Rect(0, 0, 460, 392));
            w.Close();
        });

        Check("ColorPickerDialog: builds", () =>
        {
            var d = new ColorPickerDialog(null, System.Windows.Media.Color.FromRgb(0, 229, 255));
            d.Measure(new Size(288, 460));
            d.Arrange(new Rect(0, 0, 288, 460));
            d.Close();
        });

        Check("PromptDialog: builds", () =>
        {
            var d = new PromptDialog(null, "t", "x", "ok", "cancel");
            d.Measure(new Size(340, 200));
            d.Arrange(new Rect(0, 0, 340, 200));
            d.Close();
        });

        // Both languages: the RTL pass swaps FlowDirection on every template.
        foreach (var lang in new[] { "en", "fa" })
        {
            var l = lang;
            Check($"MainWindow: builds in {l}", () =>
            {
                L10n.Set(l);
                var w = new MainWindow();
                w.Measure(new Size(560, 700));
                w.Arrange(new Rect(0, 0, 560, 700));
                w.Close();
            });
        }
        L10n.Set("en");

        // Every effect must have a catalog entry, or its chip silently disappears.
        Check("effects: every EffectType has a UI tile", () =>
        {
            var missing = Enum.GetValues<Effects.EffectType>()
                              .Where(t => !MainWindow.CatalogTypes.Contains(t))
                              .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("no tile for: " + string.Join(", ", missing));
        });

        // Localisation completeness: fa must define every key en defines, and vice versa.
        Check("l10n: en and fa cover the same keys", () =>
        {
            var diff = L10n.MissingKeys();
            if (diff.Count > 0) throw new InvalidOperationException("missing: " + string.Join(", ", diff));
        });

        // Every icon must exist in the font, or the button renders as an empty box.
        Check("glyphs: every MDL2 codepoint exists", () =>
        {
            var missing = GlyphCheck.Missing();
            if (missing.Count > 0) throw new InvalidOperationException("missing glyphs: " + string.Join(", ", missing));
        });

        // The app must never demand elevation: the manifest stays asInvoker and nothing in the
        // startup path may relaunch itself elevated.
        Check("elevation: not elevated when started normally", () =>
        {
            if (SDK.Elevation.IsElevated)
                throw new InvalidOperationException("this run IS elevated — asInvoker behaviour unverified");
        });

        // The lighting engine must be INSIDE the exe: shipping a vendor folder next to the app was
        // the thing the user asked to get rid of.
        Check("engine: bundle is embedded in the assembly", () =>
        {
            if (!SDK.EngineBundle.IsEmbedded)
                throw new InvalidOperationException($"resource {SDK.EngineBundle.ResourceName} is missing");
            long size = SDK.EngineBundle.EmbeddedSize();
            if (size < 5_000_000)
                throw new InvalidOperationException($"embedded bundle is only {size} bytes — engine tree is incomplete");
        });

        Check("engine: bundle unpacks and yields OpenRGB.exe", () =>
        {
            var exe = SDK.EngineBundle.EnsureExtracted();
            if (!System.IO.File.Exists(exe))
                throw new InvalidOperationException($"extracted path does not exist: {exe}");
            // The SMBus modules are what make RGB RAM possible; a truncated zip would still
            // produce an exe, so check one of them explicitly.
            var smbus = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "SmbusI801.bin");
            if (!System.IO.File.Exists(smbus))
                throw new InvalidOperationException("SmbusI801.bin missing from the unpacked engine");
        });

        // The hardware page must be able to enumerate USB without elevation, or its whole reason
        // for existing (explaining missing devices) fails silently.
        Check("usbscan: enumerates devices with VID/PID", () =>
        {
            var devices = Diag.UsbScan.Scan();
            if (devices.Count == 0)
                throw new InvalidOperationException("no USB devices enumerated");
            if (!devices.Any(d => d.Vid != 0 && d.Pid != 0))
                throw new InvalidOperationException("no device produced a usable VID:PID");
        });

        Check("usbscan: VID/PID parser", () =>
        {
            if (!Diag.UsbScan.TryParseVidPid(@"USB\VID_30FA&PID_1140&MI_01\7&273E5AD2&1&0001",
                                             out var vid, out var pid) || vid != 0x30FA || pid != 0x1140)
                throw new InvalidOperationException("failed to parse a real instance id");
            if (Diag.UsbScan.TryParseVidPid(@"ROOT\SYSTEM\0000", out _, out _))
                throw new InvalidOperationException("parsed a non-USB instance id");
        });

        Check("support matrix: VID/PID is read from the engine location string", () =>
        {
            // Real location strings from this rig's engine (protocol v5).
            const string hid = @"HID: \\?\HID#VID_1B1C&PID_0C32&MI_00#8&376e446a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            if (Diag.SupportMatrix.VidPidFromLocation(hid) != "1B1C:0C32")
                throw new InvalidOperationException("failed to read VID:PID from a HID location");

            const string aura = @"HID: \\?\HID#VID_0B05&PID_18F3&MI_02#7&ca0c55&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            if (Diag.SupportMatrix.VidPidFromLocation(aura) != "0B05:18F3")
                throw new InvalidOperationException("failed to read VID:PID from the Aura location");

            // SMBus RAM has no VID/PID and must not be forced into one.
            if (Diag.SupportMatrix.VidPidFromLocation("I2C: i801, address 0x71") is not null)
                throw new InvalidOperationException("invented a VID:PID for an SMBus device");
            if (Diag.SupportMatrix.VidPidFromLocation("") is not null)
                throw new InvalidOperationException("invented a VID:PID for an empty location");
        });

        Check("support matrix: nothing is called unsupported while the engine is offline", () =>
        {
            // The --uishot pass runs with no SDK connection and used to label the ASUS board and
            // the Corsair hub "not controllable" — a lie about hardware that works.
            var offline = Diag.SupportMatrix.Build(new List<SDK.RgbController>(), false, engineConnected: false);
            if (offline.Any(r => r.State == Diag.SupportState.Unsupported))
                throw new InvalidOperationException("claimed a device is unsupported with no engine connected");
            if (!offline.Any(r => r.State == Diag.SupportState.Unknown))
                throw new InvalidOperationException("no device was reported as unknown");

            // With a connection but an empty controller list, "unsupported" IS the honest answer.
            var online = Diag.SupportMatrix.Build(new List<SDK.RgbController>(), false, engineConnected: true);
            if (online.Any(r => r.State == Diag.SupportState.Unknown))
                throw new InvalidOperationException("reported unknown while the engine was connected");
        });

        Console.WriteLine(failed == 0 ? "\nALL UI TESTS PASSED" : $"\n{failed} UI TEST(S) FAILED");
        return failed == 0 ? 0 : 1;
    }
}
