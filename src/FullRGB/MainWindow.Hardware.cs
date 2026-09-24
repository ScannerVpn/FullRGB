using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullRGB.Config;
using FullRGB.Diag;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
// MainWindow inherits FrameworkElement.HorizontalAlignment/VerticalAlignment PROPERTIES, so the
// bare enum names resolve to those members (CS0176). Alias them to the types explicitly.
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace FullRGB;

/// <summary>
/// The "Hardware" page: every RGB-capable device on this PC and, for the ones FullRGB cannot
/// drive, WHY. Front and centre in the nav rail, not buried in Advanced — the user's first run
/// should answer "why is my mouse missing?" without hunting through settings.
/// </summary>
public partial class MainWindow
{
    private void BuildHardwarePage()
    {
        if (HwList is null) return;
        HwList.Children.Clear();

        // The setup rows and the explanation card live on this page too (static XAML below
        // HwList), so refresh their text every time the page is shown.
        RefreshAdvanced();
        AdvancedHdr.Text = L10n.T("hw.setupTitle");
        WhyHdr.Text = L10n.T("hw.whyTitle");
        Why1Txt.Text = L10n.T("hw.why1");
        Why2Txt.Text = L10n.T("hw.why2");
        Why3Txt.Text = L10n.T("hw.why3");

        var controllers = _client?.Controllers ?? new List<SDK.RgbController>();
        bool smbusFailed = _mgr?.LastRunHadSmbusFailure() ?? false;
        // Without a live SDK connection there is no controller list, so nothing can be called
        // unsupported — see SupportMatrix.Build.
        bool engineConnected = _client is { Connected: true };
        List<PeripheralReport> report;
        try
        {
            report = SupportMatrix.Build(controllers, smbusFailed, engineConnected);
        }
        catch (Exception e)
        {
            // A USB enumeration failure must not take the page down.
            HwList.Children.Add(Line(L10n.T("status.failed", e.Message), "Warn"));
            return;
        }

        // Engine card first: what is driving the lights, and how.
        HwList.Children.Add(EngineCard());

        AddGroup(L10n.T("hw.controlled"), report.Where(r => r.State == SupportState.Controlled), "Ok");
        AddGroup(L10n.T("hw.needsAction"), report.Where(r => r.State == SupportState.NeedsElevation), "Warn");
        AddGroup(L10n.T("hw.unsupported"), report.Where(r => r.State == SupportState.Unsupported), "Faint");
        AddGroup(L10n.T("hw.unknown"), report.Where(r => r.State == SupportState.Unknown), "Faint");

        HwList.Children.Add(BuildCommunityHidCard(report));
    }

    /// <summary>
    /// Community protocols for driverless mice/keeps: import a shared JSON definition, probe
    /// read-only, and — only behind two explicit confirmations plus the global switch — paint
    /// one solid colour. The card also annotates the unsupported rows that have a protocol.
    /// </summary>
    private Border BuildCommunityHidCard(List<PeripheralReport> report)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(L10n.T("hid.title")));
        panel.Children.Add(Line(L10n.T("hid.explain"), "Muted"));

        var (protocols, errors) = Hid.CommunityStore.Load();
        // A HID enumeration failure must degrade to "no collections found" — the card's buttons
        // then report "device not present" instead of taking the whole page (and the app) down.
        List<Hid.HidBridge.HidCollection> hid;
        try
        {
            hid = Hid.HidBridge.Enumerate();
        }
        catch (Exception e)
        {
            Diag.AppLog.Exception("community HID enumerate", e);
            hid = new List<Hid.HidBridge.HidCollection>();
            panel.Children.Add(Line(L10n.T("hid.enumFailed", e.Message), "Warn"));
        }

        foreach (var proto in protocols)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            string mode = proto.ExperimentalWrite ? L10n.T("hid.modeWrite") : L10n.T("hid.modeRead");
            info.Children.Add(new TextBlock
            {
                Text = $"{proto.Name}  ·  {proto.VidPid}",
                Style = (Style)FindResource("Txt"),
                FontSize = 11.5,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{proto.Source}  ·  {mode}  ·  sha256:{proto.Sha256}",
                Style = (Style)FindResource("FaintTxt"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var probeBtn = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = L10n.T("hid.probe"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var p = proto;
            probeBtn.Click += (_, _) => ProbeCommunityDevice(p, hid);
            actions.Children.Add(probeBtn);
            if (p.Paint is not null)
            {
                var paintBtn = new Button
                {
                    Style = (Style)FindResource("Btn"),
                    Content = L10n.T("hid.paint"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Warn"),
                };
                paintBtn.Click += (_, _) => TestPaintCommunity(p, hid, probeBtn);
                actions.Children.Add(paintBtn);
            }
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            panel.Children.Add(row);
        }

        foreach (var err in errors)
            panel.Children.Add(Line(L10n.T("hid.badFile", err), "Warn"));

        // Authoring aid: every collection a protocol file COULD address. This is the list someone
        // needs to start writing one, and the numbers come from the same enumeration the matcher
        // uses, so they cannot drift. Only vendor pages with a readable report qualify - a standard
        // keyboard/consumer collection has nothing to write to.
        var candidates = hid.Where(c => c.UsagePage is >= 0xFF00 and <= 0xFFFF)
                            .Where(c => c.FeatureLength > 0 || c.OutputLength > 0)
                            .ToList();
        if (candidates.Count > 0)
        {
            panel.Children.Add(Line(L10n.T("hid.candidates"), "Muted"));
            foreach (var cand in candidates)
            {
                var candRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 3, 0, 0),
                };
                candRow.Children.Add(new TextBlock
                {
                    Text = $"{cand.VidPid}   usagePage 0x{cand.UsagePage:X4} / usage 0x{cand.Usage:X4}   " +
                           $"feature {cand.FeatureLength}   output {cand.OutputLength}",
                    Style = (Style)FindResource("FaintTxt"),
                    FontSize = 10.5,
                    VerticalAlignment = VAlign.Center,
                });
                var copyBtn = new Button
                {
                    Style = (Style)FindResource("Btn"),
                    Content = L10n.T("hid.copySkeleton"),
                    FontSize = 10.5,
                    Margin = new Thickness(10, 0, 0, 0),
                    Padding = new Thickness(10, 4, 10, 4),
                };
                var captured = cand;
                copyBtn.Click += (_, _) => CopyProtocolSkeleton(captured);
                candRow.Children.Add(copyBtn);
                panel.Children.Add(candRow);
            }
        }

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var importBtn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = L10n.T("hid.import"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        importBtn.Click += (_, _) => ImportCommunityProtocol();
        btnRow.Children.Add(importBtn);
        var folderBtn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = L10n.T("hid.folder"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        folderBtn.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Hid.CommunityStore.Dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Hid.CommunityStore.Dir)
                { UseShellExecute = true });
            }
            catch { }
        };
        btnRow.Children.Add(folderBtn);
        var writeToggle = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = App.Settings.HidExperimentalWrite ? L10n.T("hid.writeOn") : L10n.T("hid.writeOff"),
            FontSize = 11,
            Foreground = (Brush)FindResource(App.Settings.HidExperimentalWrite ? "Warn" : "Muted"),
            ToolTip = L10n.T("hid.writeTip"),
        };
        writeToggle.Click += (_, _) =>
        {
            try
            {
                if (!App.Settings.HidExperimentalWrite)
                {
                    if (!ConfirmDialog.Ask(this, L10n.T("hid.writeWarn1"), L10n.T("hid.writeContinue"), danger: true))
                        return;
                    if (PromptDialog.Ask(this, L10n.T("hid.writeWarn2"), "") is not { } typed
                        || !typed.Trim().Equals("FULLRGB", StringComparison.OrdinalIgnoreCase))
                    {
                        SetStatus(L10n.T("hid.writeCancelled"), StatusKind.Warn);
                        return;
                    }
                    App.Settings.HidExperimentalWrite = true;
                    Diag.AppLog.Warn("community HID experimental WRITES enabled by the user");
                }
                else
                {
                    App.Settings.HidExperimentalWrite = false;
                    Diag.AppLog.Info("community HID experimental writes disabled");
                }
                ProfileStore.Save(App.Settings);
                SetStatus(L10n.T(App.Settings.HidExperimentalWrite ? "hid.writeOn" : "hid.writeOff"), StatusKind.Info);
                BuildHardwarePage();
            }
            catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
        };
        btnRow.Children.Add(writeToggle);
        panel.Children.Add(btnRow);

        // Annotate unsupported rows that a community protocol covers, right in the group list.
        foreach (var r in report.Where(r => r.State == SupportState.Unsupported))
        {
            var proto = Hid.CommunityStore.Find(protocols, ParseVid(r.VidPid), ParsePid(r.VidPid));
            if (proto is null) continue;
            panel.Children.Add(Line(L10n.T("hid.covers", r.VidPid, proto.Name), "Ok"));
        }

        return new Border
        {
            Style = (Style)FindResource("CardBd"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
        };
    }

    private static ushort ParseVid(string vidpid)
        => ushort.TryParse((vidpid ?? "").Split(':').FirstOrDefault(), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : (ushort)0;
    private static ushort ParsePid(string vidpid)
        => ushort.TryParse((vidpid ?? "").Split(':').Skip(1).FirstOrDefault(), System.Globalization.NumberStyles.HexNumber, null, out var p) ? p : (ushort)0;

    /// <summary>
    /// Best HID collection for a protocol: usagePage/usage match first, then any collection of
    /// that VID:PID. <paramref name="exact"/> is false when that fallback was used — a composite
    /// device (keyboard + hub, a mouse with several interfaces) exposes several collections, and
    /// the report may then land on the wrong one, so the caller must say so out loud.
    /// </summary>
    private static Hid.HidBridge.HidCollection? FindCollection(Hid.HidProtocolFile proto,
        List<Hid.HidBridge.HidCollection> hid, int? usagePage, int? usage, out bool exact)
    {
        var match = hid.FirstOrDefault(c => c.Vid == proto.VidNum && c.Pid == proto.PidNum
                                           && (usagePage is null || c.UsagePage == usagePage)
                                           && (usage is null || c.Usage == usage));
        if (match is not null) { exact = true; return match; }
        exact = false;
        return hid.FirstOrDefault(c => c.Vid == proto.VidNum && c.Pid == proto.PidNum);
    }

    /// <summary>READ-ONLY probe: one feature report, logged and shown in the status line.</summary>
    private void ProbeCommunityDevice(Hid.HidProtocolFile proto, List<Hid.HidBridge.HidCollection> hid)
    {
        try
        {
            var col = FindCollection(proto, hid, proto.Probe?.UsagePage, proto.Probe?.Usage, out bool exact);
            if (col is null)
            {
                SetStatus(L10n.T("hid.deviceNotFound", proto.VidPid), StatusKind.Warn);
                return;
            }
            // A read from the wrong collection is harmless but tells the author nothing useful,
            // so log it instead of letting them debug a protocol that was never reached.
            if (!exact)
                Diag.AppLog.Warn($"community probe {proto.VidPid}: usagePage/usage did not match — " +
                                 $"falling back to collection {col.Path}");
            var probe = proto.Probe ?? new Hid.HidProbe { ReportId = 0, Length = 8 };
            var (data, error) = Hid.HidBridge.Probe(col, probe.ReportId, probe.Length);
            if (data is null)
            {
                Diag.AppLog.Warn($"community probe {proto.VidPid}: {error}");
                SetStatus(L10n.T("hid.probeFailed", error), StatusKind.Error);
                return;
            }
            string hex = string.Join(" ", data.Take(32).Select(b => b.ToString("X2")));
            Diag.AppLog.Info($"community probe {proto.VidPid} {col.Path}: {hex}");
            SetStatus(L10n.T("hid.probeOk", hex), StatusKind.Ok);
        }
        catch (Exception e)
        {
            Diag.AppLog.Exception("community probe", e);
            SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error);
        }
    }

    /// <summary>
    /// One guarded test paint (solid colour from the current profile). Requires the global
    /// experimental-write switch AND a fresh double confirmation EVERY click — the second one
    /// typed out — because the device firmware is unknown to us and this is exactly the
    /// "guessed SET_FEATURE payload" risk the README warns about, offered only to users who
    /// know why they want it. The typed step is the safety model documented in
    /// <see cref="Hid.HidProtocolFile"/>; it must not be softened.
    /// </summary>
    private void TestPaintCommunity(Hid.HidProtocolFile proto, List<Hid.HidBridge.HidCollection> hid, Button owner)
    {
        try
        {
            if (!App.Settings.HidExperimentalWrite)
            {
                SetStatus(L10n.T("hid.writeNeeded"), StatusKind.Warn);
                return;
            }
            var col = FindCollection(proto, hid, proto.Paint?.UsagePage, proto.Paint?.Usage, out bool exact);
            if (col is null)
            {
                SetStatus(L10n.T("hid.deviceNotFound", proto.VidPid), StatusKind.Warn);
                return;
            }
            if (!exact)
                Diag.AppLog.Warn($"community paint {proto.VidPid}: usagePage/usage did not match — " +
                                 $"falling back to collection {col.Path}");
            // Confirmation 1 of 2: the plain yes/no, with a stronger wording when the report is
            // about to go to a fallback collection rather than the one the protocol named.
            if (!ConfirmDialog.Ask(this,
                    L10n.T(exact ? "hid.paintWarn" : "hid.paintWarnFallback", proto.Name),
                    L10n.T("hid.paintContinue"), danger: true))
                return;
            // Confirmation 2 of 2: typed, so it cannot be dismissed by muscle memory.
            if (PromptDialog.Ask(this, L10n.T("hid.paintWarn2", proto.VidPid), "") is not { } typed
                || !typed.Trim().Equals(proto.VidPid, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(L10n.T("hid.paintCancelled"), StatusKind.Warn);
                return;
            }
            PushEdit();
            string hex = CurrentProfile().GlobalEffect.ColorHex;
            var color = (System.Drawing.Color)new System.Drawing.ColorConverter().ConvertFromString(hex)!;
            var (ok, error) = Hid.HidBridge.PaintSolid(col, proto, color);
            if (ok)
            {
                Diag.AppLog.Info($"community test paint {proto.VidPid} {hex}");
                SetStatus(L10n.T("hid.paintOk", proto.VidPid), StatusKind.Ok);
            }
            else
            {
                Diag.AppLog.Warn($"community paint {proto.VidPid}: {error}");
                SetStatus(L10n.T("hid.probeFailed", error), StatusKind.Error);
            }
        }
        catch (Exception e)
        {
            Diag.AppLog.Exception("community paint", e);
            SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error);
        }
    }

    /// <summary>
    /// Puts a ready-to-edit protocol skeleton on the clipboard, pre-filled with the measured
    /// usagePage/usage/report lengths. Probe-only by construction: a skeleton must never hand
    /// someone a write section they have not verified against captured traffic themselves.
    /// </summary>
    private void CopyProtocolSkeleton(Hid.HidBridge.HidCollection c)
    {
        try
        {
            System.Windows.Clipboard.SetText(Hid.HidBridge.ProtocolSkeleton(c));
            Diag.AppLog.Info($"protocol skeleton copied for {c.VidPid} usagePage 0x{c.UsagePage:X4}");
            SetStatus(L10n.T("hid.skeletonCopied", c.VidPid), StatusKind.Ok);
        }
        catch (Exception e)
        {
            SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error);
        }
    }

    /// <summary>Validates then copies a shared protocol file into the store.</summary>
    private void ImportCommunityProtocol()    {
        try
        {
            var dlg = new System.Windows.Forms.OpenFileDialog { Filter = "Protocol JSON|*.json" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var proto = Hid.CommunityStore.Import(dlg.FileName, out var error);
            if (proto is null)
            {
                SetStatus(L10n.T("hid.badFile", error), StatusKind.Error);
                return;
            }
            Diag.AppLog.Info($"community protocol imported: {proto.Name} ({proto.VidPid})");
            SetStatus(L10n.T("hid.imported", proto.Name), StatusKind.Ok);
            BuildHardwarePage();
        }
        catch (Exception e)
        {
            SetStatus(L10n.T("hid.badFile", e.Message), StatusKind.Error);
        }
    }

    /// <summary>Card describing the bundled engine: what it is, where it runs, how it was started.</summary>
    private Border EngineCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(L10n.T("hw.engineTitle")));
        panel.Children.Add(Line(L10n.T("hw.engineWhat"), "Muted"));

        // How the engine was reached this session.
        string how = _mgr is null
            ? L10n.T("hw.engineNotRunning")
            : _mgr.StartedViaTask ? L10n.T("hw.engineViaTask")
            : _mgr.AttachedToExisting ? L10n.T("hw.engineAttached")
            : L10n.T("hw.engineOwned");
        panel.Children.Add(Line(how, "Faint"));

        // Whether it actually has SMBus access — decided by EVIDENCE (are DRAM controllers
        // present?), not by which launch path we took. An engine we merely attached to can
        // already be elevated, and an engine we started via the task could still fail PawnIO.
        bool dram = _client?.Controllers.Any(c => c.Kind == SDK.RgbDeviceType.DRAM) == true;
        if (dram)
            panel.Children.Add(Line(L10n.T("hw.engineSmbusOk"), "Ok"));
        else if (_mgr is not null)
            panel.Children.Add(Line(L10n.T("hw.engineSmbusNo"), "Faint"));

        // Prove the "inside the app" claim with the real numbers instead of asserting it.
        if (SDK.EngineBundle.IsEmbedded)
            panel.Children.Add(Line(
                L10n.T("hw.engineEmbedded", (SDK.EngineBundle.EmbeddedSize() / 1048576.0).ToString("0.#")),
                "Faint"));

        // Bundled engine version (from OpenRGB.exe itself) — the number an update check compares.
        string engVer = EngineVersion();
        if (!string.IsNullOrEmpty(engVer))
            panel.Children.Add(Line(L10n.T("hw.engineVersion", engVer), "Faint"));

        if (_client is not null)
            panel.Children.Add(Line(L10n.T("hw.engineProtocol", _client.ProtocolVersion), "Faint"));

        // Link to the engine's own log — the only place that explains a detection failure fully.
        var logBtn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = L10n.T("hw.openLog"),
            FontSize = 11,
            HorizontalAlignment = HAlign.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        logBtn.Click += (_, _) =>
        {
            try
            {
                var dir = _mgr?.LogDir();
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        };
        panel.Children.Add(logBtn);

        // Hardware report (for bug reports / driver requests) + upstream update check.
        var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var copyBtn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = L10n.T("hw.copyReport"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(BuildHardwareReport());
                SetStatus(L10n.T("hw.copied"), StatusKind.Ok);
            }
            catch (Exception e) { SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error); }
        };
        rowBtns.Children.Add(copyBtn);
        var updBtn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = L10n.T("hw.checkUpdate"),
            FontSize = 11,
        };
        updBtn.Click += async (_, _) =>
        {
            updBtn.IsEnabled = false;
            try { SetStatus(await CheckEngineUpdateAsync(), StatusKind.Info); }
            catch (Exception e) { SetStatus(L10n.T("hw.updateCheckFailed", e.Message), StatusKind.Error); }
            finally { updBtn.IsEnabled = true; }
        };
        rowBtns.Children.Add(updBtn);
        var zipBtn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = L10n.T("hw.exportDiagnostics"),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = L10n.T("hw.exportDiagnosticsTip"),
        };
        zipBtn.Click += (_, _) => ExportDiagnostics_Click();
        rowBtns.Children.Add(zipBtn);
        panel.Children.Add(rowBtns);

        return new Border
        {
            Style = (Style)FindResource("CardBd"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
        };
    }

    /// <summary>Builds the bug-report zip next to where the user picks and says where it went.</summary>
    private void ExportDiagnostics_Click()
    {
        try
        {
            var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "Zip|*.zip",
                FileName = $"FullRGB-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var controllers = _client?.Controllers;
            string path = Diag.DiagnosticsExport.Build(dlg.FileName,
                () => Diag.DiagnosticsExport.SupportMatrixText(controllers));
            Diag.AppLog.Info("diagnostics exported: " + path);
            SetStatus(L10n.T("hw.diagnosticsDone"), StatusKind.Ok);
        }
        catch (Exception e)
        {
            SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error);
        }
    }

    /// <summary>Bundled OpenRGB.exe version (ProductVersion), or "" when unavailable.</summary>
    internal static string EngineVersion()
    {
        try
        {
            string exe = SDK.OpenRgbProcessManager.DefaultExePath();
            if (!System.IO.File.Exists(exe)) return "";
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return (vi.ProductVersion ?? vi.FileVersion ?? "").Trim();
        }
        catch { return ""; }
    }

    /// <summary>Plain-text inventory for bug reports: app + engine + every USB VID:PID + controllers.</summary>
    internal string BuildHardwareReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"FullRGB {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"Engine bundled: {EngineVersion()}");
        sb.AppendLine($"Engine task: registered={Setup.EngineTask.IsRegistered()} " +
                      $"matchesThisInstall={Setup.EngineTask.MatchesInstall(SDK.OpenRgbProcessManager.DefaultExePath())} " +
                      $"pawnio={Setup.DependencyManager.IsPawnIoInstalled()} elevated={SDK.Elevation.IsElevated}");
        sb.AppendLine("Controllers:");
        foreach (var c in _client?.Controllers ?? Enumerable.Empty<SDK.RgbController>())
            sb.AppendLine($"  [{c.Index}] {c.Name} leds={c.LedCount} vendor={c.Vendor} loc={c.Location}");
        sb.AppendLine("USB/HID devices:");
        try
        {
            foreach (var d in Diag.UsbScan.Scan())
                sb.AppendLine($"  {d.VidPid}  {d.DeviceClass,-12} {d.Label}");
        }
        catch (Exception e) { sb.AppendLine($"  (scan failed: {e.Message})"); }
        return sb.ToString();
    }

    private static readonly System.Net.Http.HttpClient UpdateHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Best-effort check of the latest upstream OpenRGB release (user-initiated only).</summary>
    internal static async Task<string> CheckEngineUpdateAsync()
    {
        using var req = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "https://api.github.com/repos/openrgb/OpenRGB/releases/latest");
        req.Headers.UserAgent.ParseAdd("FullRGB-update-check");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await UpdateHttp.SendAsync(req).ConfigureAwait(true);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
        if (!m.Success) return L10n.T("hw.updateCheckFailed", "no tag_name");
        string latest = m.Groups[1].Value.TrimStart('v', 'V');
        string bundled = EngineVersion().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(bundled)) return L10n.T("hw.latestUpstream", m.Groups[1].Value);
        // Numeric prefix compare ("9.0" vs "9.0rc1" → equal prefix = up to date).
        string Norm(string v) => new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Norm(latest) == Norm(bundled) && !string.IsNullOrEmpty(Norm(latest))
            ? L10n.T("hw.updateCurrent", bundled)
            : L10n.T("hw.updateAvailable", m.Groups[1].Value, bundled.Length > 0 ? bundled : "-");
    }

    private void AddGroup(string title, IEnumerable<PeripheralReport> items, string dotBrush)
    {
        var rows = items.ToList();
        if (rows.Count == 0) return;

        var panel = new StackPanel();
        panel.Children.Add(Header($"{title} ({rows.Count})"));

        foreach (var r in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7,
                Fill = (Brush)FindResource(dotBrush),
                Margin = new Thickness(2, 6, 10, 0),
                VerticalAlignment = VAlign.Top,
            });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = r.Label,
                Style = (Style)FindResource("Txt"),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            // VID:PID is what a bug report needs, so show it rather than hide it.
            var detail = string.IsNullOrEmpty(r.Reason) ? r.VidPid : $"{r.VidPid} · {r.Reason}";
            text.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)FindResource("FaintTxt"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            panel.Children.Add(grid);
        }

        HwList.Children.Add(new Border
        {
            Style = (Style)FindResource("CardBd"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
        });
    }

    private TextBlock Header(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("SectionHdr"),
        Margin = new Thickness(0, 0, 0, 9),
    };

    private TextBlock Line(string text, string brush) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource(brush),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 5),
    };
}
