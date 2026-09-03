using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullRGB.Diag;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
// MainWindow inherits FrameworkElement.HorizontalAlignment/VerticalAlignment PROPERTIES, so the
// bare enum names resolve to those members (CS0176). Alias them to the types explicitly.
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

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

        return new Border
        {
            Style = (Style)FindResource("CardBd"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
        };
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
