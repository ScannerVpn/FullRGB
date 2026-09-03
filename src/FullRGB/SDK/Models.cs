namespace FullRGB.SDK;

/// <summary>OpenRGB device_type enum (verified against the server's own ordering).</summary>
public enum RgbDeviceType
{
    Motherboard = 0, DRAM = 1, GPU = 2, Cooler = 3, LedStrip = 4,
    Keyboard = 5, Mouse = 6, MouseMat = 7, Headset = 8, HeadsetStand = 9,
    Gamepad = 10, Light = 11, Speaker = 12, Virtual = 13, Storage = 14,
    Case = 15, Microphone = 16, Accessory = 17, Keypad = 18, Unknown = 19,
}

/// <summary>Immutable snapshot of one RGB controller as parsed from the OpenRGB SDK.</summary>
public sealed class RgbController
{
    public int Index { get; init; }
    public uint DeviceType { get; init; }
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public int ActiveMode { get; init; }
    public List<RgbMode> Modes { get; init; } = new();
    public List<RgbZone> Zones { get; init; } = new();
    public List<string> LedNames { get; init; } = new();
    public bool ParseFailed { get; init; }
    public int LedCount => LedNames.Count;

    public RgbDeviceType Kind =>
        DeviceType <= (uint)RgbDeviceType.Unknown ? (RgbDeviceType)DeviceType : RgbDeviceType.Unknown;

    /// <summary>Index of the mode named "Direct" (per-LED software control), or -1.</summary>
    public int DirectModeIndex
    {
        get
        {
            for (int i = 0; i < Modes.Count; i++)
                if (Modes[i].Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>True when the device is currently in a software-paintable mode.</summary>
    public bool InDirectMode => DirectModeIndex >= 0 && ActiveMode == DirectModeIndex;

    /// <summary>Stable identity used as a profile key (survives index reordering).</summary>
    public string Key => string.IsNullOrEmpty(Location) ? Name : $"{Name}@{Location}";
}

public sealed record RgbMode
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Value { get; init; }
    public uint Flags { get; init; }
    public uint SpeedMin, SpeedMax, Speed;
    public uint Direction;
    public uint BrightnessMin, BrightnessMax, Brightness;
    public uint ColorsMin, ColorsMax;
    public uint ColorMode;
    public byte[][] Colors { get; init; } = Array.Empty<byte[]>();
}

public sealed class RgbZone
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int ZoneType { get; init; }      // 0 = single, 1 = linear, 2 = matrix
    public uint LedsMin, LedsMax, LedsCount;
    public uint MatrixHeight, MatrixWidth;

    /// <summary>Offset of this zone's first LED inside the device-wide LED buffer.</summary>
    public int StartIndex { get; set; }

    public bool IsResizable => LedsMax > LedsMin;
    public bool IsMatrix => ZoneType == 2;
}

public static class Pkt
{
    // Wire values verified against openrgb-python's PacketType enum (they match the OpenRGB server).
    public const uint REQUEST_CONTROLLER_COUNT = 0;
    public const uint REQUEST_CONTROLLER_DATA = 1;
    public const uint REQUEST_PROTOCOL_VERSION = 40;
    public const uint SET_CLIENT_NAME = 50;
    public const uint DEVICE_LIST_UPDATED = 100;
    public const uint RESIZE_ZONE = 1000;
    public const uint UPDATE_LEDS = 1050;
    public const uint UPDATE_ZONE_LEDS = 1051;
    public const uint UPDATE_SINGLE_LED = 1052;
    public const uint SET_CUSTOM_MODE = 1100;
    public const uint UPDATE_MODE = 1101;
    public const uint SAVE_MODE = 1102;
}

// mode flags + color modes
public static class ModeFlags
{
    public const uint HAS_BRIGHTNESS = 1u << 3;
    public const uint HAS_SPEED = 1u << 4;
    public const uint HAS_DIRECTION_LR = 1u << 5;
    public const uint HAS_DIRECTION_UPDOWN = 1u << 6;
    public const uint HAS_DIRECTION_HV = 1u << 7;
    public const uint HAS_PERCENT = 1u << 8;
    public const uint HAS_MODE_SPECIFIC_COLOR = 1u << 9;
    public const uint MODE_COLORS_NONE = 0;
    public const uint MODE_COLORS_PER_LED = 1;
    public const uint MODE_COLORS_MODE_SPECIFIC = 2;
    public const uint MODE_COLORS_RANDOM = 3;
}
