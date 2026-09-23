namespace FullRGB.Sensors;

/// <summary>
/// State fed by the automation interfaces (HTTP <c>POST /api/event</c>, the named pipe
/// <c>game-event</c> command, the CLI <c>--game-event</c> verb) and consumed by the
/// GamePulse effect. Any external tool can drive it — a game mod, an AHK script reading
/// the screen, a Lua overlay, a WebSocket bridge. Values are global to the session.
///
/// Thread safety: writers are server threads, readers are the render loops — everything
/// goes through volatile fields / Interlocked, never through locks (a render loop must
/// never wait behind an HTTP request). Every field below is backed by an explicit
/// <see cref="Volatile"/> or <see cref="Interlocked"/> access; the doubles are stored as
/// their 64-bit pattern so a reader can never observe a torn value.
/// </summary>
public static class GameEventState
{
    // Backing fields. Longs use Volatile, doubles use Interlocked on their bit pattern
    // (a double is 8 bytes and is NOT guaranteed atomic on 32-bit; "volatile double" is
    // not even legal C#).
    private static string _lastEvent = "";
    private static long _lastEventAtMs;
    private static long _healthBits = BitConverter.DoubleToInt64Bits(1);
    private static int _hasHealth;                       // 0/1, read as bool
    private static long _hitAtMs;
    private static long _hitStrengthBits;
    private static long _deathAtMs;

    /// <summary>LongTicks of the last accepted event (Environment.TickCount64 based).</summary>
    public static string LastEvent => Volatile.Read(ref _lastEvent);
    public static long LastEventAtMs => Volatile.Read(ref _lastEventAtMs);

    /// <summary>0..1 (1 = healthy). Only meaningful when HasHealth.</summary>
    public static bool HasHealth => Volatile.Read(ref _hasHealth) != 0;

    /// <summary>Current health value (1 when unknown) — safe to read from any thread.</summary>
    public static double Health => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _healthBits));

    /// <summary>Absolute time (TickCount64) of the last hit/death flash and its 0..1 strength.</summary>
    public static long HitAtMs => Volatile.Read(ref _hitAtMs);
    public static double HitStrength => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _hitStrengthBits));
    /// <summary>True when the last "death"-kind event should hold a distinct look for ~1.5 s.</summary>
    public static long DeathAtMs => Volatile.Read(ref _deathAtMs);

    private static long Now => Environment.TickCount64;

    private static void SetDouble(ref long slot, double value)
        => Interlocked.Exchange(ref slot, BitConverter.DoubleToInt64Bits(value));

    /// <summary>
    /// Pushes one event. Known names: <c>hp</c>/<c>health</c> (0..1 or 0..100),
    /// <c>hit</c>/<c>damage</c>, <c>heal</c>, <c>death</c>, <c>respawn</c>, <c>levelup</c>,
    /// <c>goal</c>, <c>explode</c>. Unknown names are still accepted (stored as LastEvent).
    /// </summary>
    public static void Push(string name, double value = double.NaN)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().ToLowerInvariant();
        Volatile.Write(ref _lastEvent, name);
        Volatile.Write(ref _lastEventAtMs, Now);
        switch (name)
        {
            case "hp":
            case "health":
                double v = value;
                // Publish the value BEFORE the flag, so a reader that sees HasHealth=true
                // is guaranteed to see the matching health reading.
                if (double.IsNaN(v)) { Volatile.Write(ref _hasHealth, 0); return; }
                if (v > 1.0) v /= 100.0;              // accept 0..100 as well as 0..1
                SetDouble(ref _healthBits, Math.Clamp(v, 0, 1));
                Volatile.Write(ref _hasHealth, 1);
                break;
            case "hit":
            case "damage":
                // Same ordering rule: strength first, timestamp second — the flash envelope
                // pairs the two, and a half-updated pair would render a wrong brightness.
                double hit = double.IsNaN(value) ? 1 : Math.Clamp(value, 0, 1);
                if (hit <= 0) hit = 1;
                SetDouble(ref _hitStrengthBits, hit);
                Volatile.Write(ref _hitAtMs, Now);
                break;
            case "death":
                SetDouble(ref _hitStrengthBits, 1);
                Volatile.Write(ref _hitAtMs, Now);
                Volatile.Write(ref _deathAtMs, Now);
                break;
            case "heal":
            case "respawn":
                SetDouble(ref _hitStrengthBits, 0.6);
                Volatile.Write(ref _hitAtMs, Now);
                Volatile.Write(ref _deathAtMs, 0L);
                break;
            case "levelup":
            case "goal":
            case "explode":
                SetDouble(ref _hitStrengthBits, 0.8);
                Volatile.Write(ref _hitAtMs, Now);
                break;
        }
    }

    /// <summary>Clears everything (profile switched away from GamePulse, user pressed reset).</summary>
    public static void Reset()
    {
        SetDouble(ref _healthBits, 1);
        Volatile.Write(ref _hasHealth, 0);
        Volatile.Write(ref _hitAtMs, 0L);
        Volatile.Write(ref _deathAtMs, 0L);
        SetDouble(ref _hitStrengthBits, 0);
        Volatile.Write(ref _lastEvent, "");
    }

    /// <summary>Flash envelope for the last hit, already decayed: 1 → 0 over ~450 ms.</summary>
    public static double FlashNow(long nowMs)
    {
        long hitAt = HitAtMs;
        long dt = nowMs - hitAt;
        if (dt < 0 || dt > 450) return 0;
        return HitStrength * (1 - dt / 450.0);
    }

    public static bool DeathHoldNow(long nowMs) => nowMs - DeathAtMs is >= 0 and < 1500;

    /// <summary>Fills the render context for this frame. Cheap, lock-free, always safe.</summary>
    public static void Fill(Effects.EffectContext ctx)
    {
        long now = Now;
        ctx.GameHasHealth = HasHealth;
        ctx.GameHealth = Health;
        ctx.GameHitFlash = FlashNow(now);
        ctx.GameDeathHold = DeathHoldNow(now);
        ctx.GameLastEvent = LastEvent;
    }
}
