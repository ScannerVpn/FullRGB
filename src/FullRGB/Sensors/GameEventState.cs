namespace FullRGB.Sensors;

/// <summary>
/// State fed by the automation interfaces (HTTP <c>POST /api/event</c>, the named pipe
/// <c>game-event</c> command, the CLI <c>--game-event</c> verb) and consumed by the
/// GamePulse effect. Any external tool can drive it — a game mod, an AHK script reading
/// the screen, a Lua overlay, a WebSocket bridge. Values are global to the session.
///
/// Thread safety: writers are server threads, readers are the render loops — everything
/// goes through volatile fields / Interlocked, never through locks (a render loop must
/// never wait behind an HTTP request).
/// </summary>
public static class GameEventState
{
    /// <summary>LongTicks of the last accepted event (Environment.TickCount64 based).</summary>
    public static string LastEvent { get; private set; } = "";
    public static long LastEventAtMs { get; private set; }

    /// <summary>0..1 (1 = healthy). Only meaningful when HasHealth.</summary>
    private static double _health = 1;
    public static bool HasHealth { get; private set; }

    /// <summary>Current health value (1 when unknown) — safe to read from any thread.</summary>
    public static double Health => _health;

    /// <summary>Absolute time (TickCount64) of the last hit/death flash and its 0..1 strength.</summary>
    public static long HitAtMs { get; private set; }
    public static double HitStrength { get; private set; }
    /// <summary>True when the last "death"-kind event should hold a distinct look for ~1.5 s.</summary>
    public static long DeathAtMs { get; private set; }

    private static long Now => Environment.TickCount64;

    /// <summary>
    /// Pushes one event. Known names: <c>hp</c>/<c>health</c> (0..1 or 0..100),
    /// <c>hit</c>/<c>damage</c>, <c>heal</c>, <c>death</c>, <c>respawn</c>, <c>levelup</c>,
    /// <c>goal</c>, <c>explode</c>. Unknown names are still accepted (stored as LastEvent).
    /// </summary>
    public static void Push(string name, double value = double.NaN)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().ToLowerInvariant();
        LastEvent = name;
        LastEventAtMs = Now;
        switch (name)
        {
            case "hp":
            case "health":
                double v = value;
                if (double.IsNaN(v)) { HasHealth = false; return; }
                if (v > 1.0) v /= 100.0;              // accept 0..100 as well as 0..1
                _health = Math.Clamp(v, 0, 1);
                HasHealth = true;
                break;
            case "hit":
            case "damage":
                HitStrength = double.IsNaN(value) ? 1 : Math.Clamp(value, 0, 1);
                if (HitStrength <= 0) HitStrength = 1;
                HitAtMs = Now;
                break;
            case "death":
                HitAtMs = Now;
                HitStrength = 1;
                DeathAtMs = Now;
                break;
            case "heal":
            case "respawn":
                HitAtMs = Now;
                HitStrength = 0.6;
                DeathAtMs = 0;
                break;
            case "levelup":
            case "goal":
            case "explode":
                HitAtMs = Now;
                HitStrength = 0.8;
                break;
        }
    }

    /// <summary>Clears everything (profile switched away from GamePulse, user pressed reset).</summary>
    public static void Reset()
    {
        _health = 1;
        HasHealth = false;
        HitAtMs = 0;
        DeathAtMs = 0;
        HitStrength = 0;
        LastEvent = "";
    }

    /// <summary>Flash envelope for the last hit, already decayed: 1 → 0 over ~450 ms.</summary>
    public static double FlashNow(long nowMs)
    {
        long dt = nowMs - HitAtMs;
        if (dt < 0 || dt > 450) return 0;
        return HitStrength * (1 - dt / 450.0);
    }

    public static bool DeathHoldNow(long nowMs) => nowMs - DeathAtMs is >= 0 and < 1500;

    /// <summary>Fills the render context for this frame. Cheap, lock-free, always safe.</summary>
    public static void Fill(Effects.EffectContext ctx)
    {
        long now = Now;
        ctx.GameHasHealth = HasHealth;
        ctx.GameHealth = _health;
        ctx.GameHitFlash = FlashNow(now);
        ctx.GameDeathHold = DeathHoldNow(now);
        ctx.GameLastEvent = LastEvent;
    }
}
