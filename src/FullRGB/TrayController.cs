using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace FullRGB;

/// <summary>
/// The notification-area icon. Created ONCE at startup and kept visible for the whole
/// session — previously it was only constructed inside HideToTray(), so if the user never
/// minimised the window the app never appeared under "show hidden icons" at all.
/// Owns a live menu (show, start/stop effects, all off, profile switch, exit).
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly Window _win;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    private ToolStripMenuItem _showItem = null!;
    private ToolStripMenuItem _toggleItem = null!;
    private ToolStripMenuItem _blackoutItem = null!;
    private ToolStripMenuItem _profilesItem = null!;
    private ToolStripMenuItem _exitItem = null!;
    private ToolStripMenuItem _gamingItem = null!;
    /// <summary>Kept as a field so Dispose can actually unsubscribe it (a fresh lambda cannot).</summary>
    private readonly System.ComponentModel.CancelEventHandler _openingHandler;

    /// <summary>Invoked when the user picks start/stop effects. Argument: true = start.</summary>
    public Action<bool>? ToggleEffects { get; set; }
    public Action? Blackout { get; set; }
    public Action<string>? SelectProfile { get; set; }
    public Func<bool>? IsEffectsRunning { get; set; }
    public Func<IEnumerable<string>>? ProfileNames { get; set; }
    public Func<string>? ActiveProfile { get; set; }
    /// <summary>True = switch every paintable zone to the Gaming effect (tray quick-toggle).</summary>
    public Action? GamingOn { get; set; }
    /// <summary>Real application exit (the window's close button only hides to tray).</summary>
    public Action? ExitApp { get; set; }

    public TrayController(Window win)
    {
        _win = win;
        _menu = new ContextMenuStrip
        {
            // dark menu so it matches the app instead of the default grey WinForms strip
            RenderMode = ToolStripRenderMode.System,
            ShowImageMargin = false,
            RightToLeft = L10n.IsRtl ? RightToLeft.Yes : RightToLeft.No,
        };
        BuildMenu();

        _icon = new NotifyIcon
        {
            Text = "FullRGB",
            Icon = LoadAppIcon(),
            Visible = true,                 // visible from the moment the app starts
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => Restore();
        _openingHandler = (_, _) => Sync();
        _menu.Opening += _openingHandler;
    }

    /// <summary>
    /// Loads Assets/app.ico. Order matters: the packed WPF resource is the crisp
    /// multi-size icon; ExtractAssociatedIcon is the single-file-publish fallback.
    /// (The old code used SystemIcons.Application, which renders as a blank placeholder.)
    /// </summary>
    public static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var res = Application.GetResourceStream(uri);
            if (res?.Stream is not null)
            {
                using var s = res.Stream;
                // request 16px explicitly: Windows' tray is 16px and downscaling the 256px
                // frame produces a mushy icon
                return new Icon(s, 16, 16);
            }
        }
        catch { }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    private void BuildMenu()
    {
        // Dispose old items — but AFTER clearing: Dispose() removes the item from its owning
        // collection, so disposing during foreach threw "Collection was modified" the moment
        // the user right-clicked the tray icon while the menu was opening.
        var old = _menu.Items.OfType<System.Windows.Forms.ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var it in old) try { it.Dispose(); } catch { }
        _menu.RightToLeft = L10n.IsRtl ? RightToLeft.Yes : RightToLeft.No;

        var header = new ToolStripMenuItem(L10n.T("tray.title")) { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        _showItem = new ToolStripMenuItem(L10n.T("tray.show"), null, (_, _) => Restore());
        _menu.Items.Add(_showItem);

        _toggleItem = new ToolStripMenuItem(L10n.T("btn.stopEffects"), null, (_, _) =>
        {
            bool running = IsEffectsRunning?.Invoke() ?? false;
            ToggleEffects?.Invoke(!running);
            Sync();
        });
        _menu.Items.Add(_toggleItem);

        _blackoutItem = new ToolStripMenuItem(L10n.T("btn.blackout"), null, (_, _) => Blackout?.Invoke());
        _menu.Items.Add(_blackoutItem);

        // Gaming quick-toggle: one click switches the whole rig to the screen-mirror effect
        // (and back to the profile) without opening the window — the "start playing NOW" path.
        _gamingItem = new ToolStripMenuItem(L10n.T("tray.gaming"), null, (_, _) => GamingOn?.Invoke());
        _menu.Items.Add(_gamingItem);

        _profilesItem = new ToolStripMenuItem(L10n.T("profile"));
        _menu.Items.Add(_profilesItem);

        _menu.Items.Add(new ToolStripSeparator());
        _exitItem = new ToolStripMenuItem(L10n.T("tray.exit"), null, (_, _) =>
        {
            _icon.Visible = false;
            if (ExitApp is not null) ExitApp();
            else Application.Current.Shutdown();
        });
        _menu.Items.Add(_exitItem);
    }

    /// <summary>Refreshes labels + the profile submenu right before the menu opens.</summary>
    private void Sync()
    {
        try
        {
            bool running = IsEffectsRunning?.Invoke() ?? false;
            _toggleItem.Text = running ? L10n.T("btn.stopEffects") : L10n.T("btn.startEffects");

            // Clear FIRST, dispose the snapshot afterwards: dropping items while enumerating
            // the live collection is exactly what crashed the menu open (see BuildMenu).
            var old = _profilesItem.DropDownItems.OfType<System.Windows.Forms.ToolStripItem>().ToArray();
            _profilesItem.DropDownItems.Clear();
            foreach (var it in old) try { it.Dispose(); } catch { }
            var names = ProfileNames?.Invoke()?.ToList() ?? new List<string>();
            var active = ActiveProfile?.Invoke();
            foreach (var name in names)
            {
                var item = new ToolStripMenuItem(name) { Checked = name == active, CheckOnClick = false };
                var captured = name;
                item.Click += (_, _) => SelectProfile?.Invoke(captured);
                _profilesItem.DropDownItems.Add(item);
            }
            _profilesItem.Enabled = names.Count > 0;
        }
        catch { /* a broken menu refresh must never crash the menu open */ }
    }

    /// <summary>Re-labels everything after a language switch.</summary>
    public void ApplyLanguage()
    {
        BuildMenu();
        Sync();
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text is capped at 63 chars by the shell
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    public void Restore()
    {
        _win.Show();
        _win.WindowState = WindowState.Normal;
        _win.Activate();
        _win.Topmost = true;
        _win.Topmost = false;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_openingHandler is not null) _menu.Opening -= _openingHandler;
        }
        catch { }
        Icon? icon = null;
        try { icon = _icon.Icon; } catch { }
        try { _icon.Visible = false; } catch { }
        try { _icon.Dispose(); } catch { }
        try { icon?.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }
    }
}
