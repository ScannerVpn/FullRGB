namespace FullRGB.Automation;

/// <summary>
/// What the control bus (CLI one-shots, the named pipe, the local HTTP API and the mobile
/// companion) is allowed to do. Implemented by <see cref="MainWindow"/> in the
/// MainWindow.Automation.cs partial. EVERY method is invoked on the UI thread — the hub
/// marshals before calling — so implementations can touch WPF state freely, but must be
/// quick (an HTTP client is blocked while they run).
/// </summary>
public interface IControlTarget
{
    /// <summary>Machine-readable session summary for `status` / GET /api/status (JSON).</summary>
    string StatusJson();

    /// <summary>Profile names in storage order.</summary>
    List<string> ProfileNames();

    /// <summary>Switches the active profile. False when the name is unknown.</summary>
    bool SetProfile(string name);

    /// <summary>Sets the GLOBAL effect of the active profile by type name
    /// ("solid", "rainbow", "gamepulse", …). False when unknown.</summary>
    bool SetEffect(string name);

    /// <summary>Sets the primary/secondary colour of the active profile's global effect ("#RRGGBB").</summary>
    bool SetColor(string hex, bool secondary = false);

    /// <summary>Global effect brightness, 0..1.</summary>
    bool SetBrightness(double v);

    /// <summary>Global effect speed, 0..1.</summary>
    bool SetSpeed(double v);

    /// <summary>true = start the effect loops, false = stop them.</summary>
    bool Power(bool on);

    /// <summary>Paints everything black once (effects keep running).</summary>
    bool Blackout();

    /// <summary>Feeds the game-event state for the GamePulse effect (name like "hit", "hp", "death").</summary>
    void PushGameEvent(string name, double value);

    /// <summary>Re-detects devices. False when no engine session exists.</summary>
    bool Rescan();

    /// <summary>6-digit PIN the companion page exchanges for the API token (regenerated every start).</summary>
    string CompanionPin { get; }

    /// <summary>Opaque API token (also written to %APPDATA%\FullRGB\api-token for local scripts).</summary>
    string ApiToken { get; }
}
