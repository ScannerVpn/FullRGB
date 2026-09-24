using System.Windows.Interop;

namespace FullRGB;

/// <summary>
/// System-wide hotkeys. An RGB app lives in the background while a game or a film is in front, so
/// an action reachable only through the tray still costs a mouse and a window switch — which is
/// exactly when "blackout now" or "next profile" is wanted. RegisterHotKey makes them work from
/// any foreground app without the app stealing focus.
/// </summary>
public partial class MainWindow
{
    private HwndSource? _hotkeySource;
    private readonly List<Setup.HotkeyManager.Action> _boundHotkeys = new();

    /// <summary>Installs the WM_HOTKEY hook and binds whatever the user configured.</summary>
    private void StartHotkeys()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            _hotkeySource ??= HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(HotkeyHook);
            ApplyHotkeys();
        }
        catch (Exception e)
        {
            Diag.AppLog.Warn("hotkeys unavailable: " + e.Message);
        }
    }

    /// <summary>Re-binds every configured hotkey. Safe to call after the user edits the settings.</summary>
    private void ApplyHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        foreach (var a in _boundHotkeys) Setup.HotkeyManager.Unregister(hwnd, a);
        _boundHotkeys.Clear();

        Bind(hwnd, Setup.HotkeyManager.Action.ToggleEffects, App.Settings.HotkeyToggleEffects, "hotkey.toggle");
        Bind(hwnd, Setup.HotkeyManager.Action.Blackout, App.Settings.HotkeyBlackout, "hotkey.blackout");
        Bind(hwnd, Setup.HotkeyManager.Action.NextProfile, App.Settings.HotkeyNextProfile, "hotkey.nextProfile");
    }

    private void Bind(IntPtr hwnd, Setup.HotkeyManager.Action action, string combo, string labelKey)
    {
        if (string.IsNullOrWhiteSpace(combo)) return;
        if (Setup.HotkeyManager.Register(hwnd, action, combo, out string error))
        {
            _boundHotkeys.Add(action);
            Diag.AppLog.Info($"hotkey {action} bound to {combo}");
            return;
        }
        // Never fail silently: a hotkey that "does nothing" is indistinguishable from a broken app.
        Diag.AppLog.Warn($"hotkey {action} '{combo}' not bound: {error}");
        SetStatus(L10n.T("hotkey.failed", L10n.T(labelKey), error), StatusKind.Warn);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Setup.HotkeyManager.WmHotkey) return IntPtr.Zero;
        // The hook can arrive on a different thread than the UI: marshal like the tray does.
        var action = (Setup.HotkeyManager.Action)wParam.ToInt32();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                switch (action)
                {
                    case Setup.HotkeyManager.Action.ToggleEffects:
                    {
                        bool running = _engine?.IsRunning == true;
                        if (running) { _engine?.Stop(); SetStatus(L10n.T("status.stopped"), StatusKind.Info); }
                        else { _engine?.Apply(CurrentProfile()); RefreshStatus(); }
                        SyncRunButtons();
                        break;
                    }
                    case Setup.HotkeyManager.Action.Blackout:
                        Blackout_Click(this, new System.Windows.RoutedEventArgs());
                        break;
                    case Setup.HotkeyManager.Action.NextProfile:
                        SwitchProfile(NextProfileName());
                        break;
                }
            }
            catch (Exception e) { Diag.AppLog.Warn($"hotkey {action} failed: {e.Message}"); }
        });
        handled = true;
        return IntPtr.Zero;
    }

    private void StopHotkeys()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var a in _boundHotkeys) Setup.HotkeyManager.Unregister(hwnd, a);
            _boundHotkeys.Clear();
            _hotkeySource?.RemoveHook(HotkeyHook);
        }
        catch { }
    }
}
