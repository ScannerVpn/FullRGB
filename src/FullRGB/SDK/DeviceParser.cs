using System.IO;

namespace FullRGB.SDK;

/// <summary>
/// Parses REQUEST_CONTROLLER_DATA payloads.
/// Wire layout verified byte-by-byte against the live OpenRGB 1.0rc3.1 server (protocol v4);
/// every device on the test rig parses to an exact end-of-payload fit.
///   u32 total_size (includes itself) | i32 device_type |
///   str name | str vendor | str description | str version | str serial | str location |
///   u16 mode_count | i32 active_mode |
///   mode[]: str name, then 12 fixed u32s
///           (value, flags, speed_min, speed_max, brightness_min, brightness_max,
///            colors_min, colors_max, speed, brightness, direction, color_mode),
///           u16 color_count, color_count*4 bytes (RGBA) |
///   u16 zone_count | zone[]: str name, i32 zone_type, u32 leds_min, u32 leds_max, u32 leds_count,
///           u16 matrix_size, (matrix_size &gt; 0: u32 height, u32 width, height*width*4 bytes),
///           u16 segment_count, segment[]: str name + 12 bytes (i32 type, u32 start_idx, u32 leds_count) |
///   u16 led_count | led[]: str name, u32 value |
///   u16 color_count | color_count*4 bytes (RGBA)
/// Strings: u16 length INCLUDING trailing NUL, then NUL-terminated UTF-8 bytes.
/// Colors on the wire are 4 bytes (R,G,B,pad).
/// </summary>
public static class DeviceParser
{
    public static RgbController Parse(int index, byte[] d)
    {
        var r = new Reader(d);
        try
        {
            r.Skip(4); // total size prefix
            int deviceType = r.I32();
            string name = r.Str();
            string vendor = r.Str();
            string description = r.Str();
            string version = r.Str();
            string serial = r.Str();
            string location = r.Str();

            ushort modeCount = r.U16();
            int activeMode = r.I32();
            var modes = new List<RgbMode>(modeCount);
            for (int m = 0; m < modeCount; m++)
            {
                string mName = r.Str();
                int value = r.I32();
                uint flags = r.U32();
                uint speedMin = r.U32(), speedMax = r.U32();
                uint bMin = r.U32(), bMax = r.U32();
                uint cMin = r.U32(), cMax = r.U32();
                uint speed = r.U32();
                uint brightness = r.U32();
                uint direction = r.U32();
                uint colorMode = r.U32();
                ushort modeColorCount = r.U16();
                r.Skip(modeColorCount * 4);
                modes.Add(new RgbMode
                {
                    Id = m,
                    Name = string.IsNullOrWhiteSpace(mName) ? $"Mode {m}" : mName,
                    Value = value,
                    Flags = flags,
                    SpeedMin = speedMin, SpeedMax = speedMax, Speed = speed,
                    BrightnessMin = bMin, BrightnessMax = bMax, Brightness = brightness,
                    ColorsMin = cMin, ColorsMax = cMax,
                    Direction = direction,
                    ColorMode = colorMode,
                    Colors = Array.Empty<byte[]>(),
                });
            }

            ushort zoneCount = r.U16();
            var zones = new List<RgbZone>(zoneCount);
            int runningStart = 0;
            for (int z = 0; z < zoneCount; z++)
            {
                string zName = r.Str();
                int zType = r.I32();
                uint min = r.U32(), max = r.U32(), count = r.U32();
                ushort matrixSize = r.U16();
                uint matH = 0, matW = 0;
                if (matrixSize > 0)
                {
                    matH = r.U32();
                    matW = r.U32();
                    r.Skip(checked((int)(matH * matW * 4)));
                }
                ushort segmentCount = r.U16();
                for (int s = 0; s < segmentCount; s++)
                {
                    r.Str();      // segment name
                    r.Skip(12);   // i32 type, u32 start_idx, u32 leds_count
                }
                zones.Add(new RgbZone
                {
                    Index = z,
                    Name = zName,
                    ZoneType = zType,
                    LedsMin = min, LedsMax = max, LedsCount = count,
                    MatrixHeight = matH, MatrixWidth = matW,
                    StartIndex = runningStart,
                });
                runningStart += (int)count;
            }

            ushort ledCount = r.U16();
            var leds = new List<string>(ledCount);
            for (int l = 0; l < ledCount; l++)
            {
                leds.Add(r.Str());
                r.Skip(4); // led value u32
            }

            ushort colorCount = r.U16();
            r.Skip(colorCount * 4);

            return new RgbController
            {
                Index = index,
                DeviceType = (uint)Math.Max(0, deviceType),
                Name = string.IsNullOrWhiteSpace(name) ? $"Device {index}" : name,
                Vendor = vendor,
                Description = description,
                Version = version,
                Serial = serial,
                Location = location,
                ActiveMode = activeMode,
                Modes = modes,
                Zones = zones,
                LedNames = leds,
            };
        }
        catch (Exception e) when (e is EndOfStreamException or OverflowException)
        {
            return new RgbController
            {
                Index = index,
                Name = "(partially readable device)",
                DeviceType = 19,
                ParseFailed = true,
            };
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _d;
        private int _p;
        public Reader(byte[] d) { _d = d; }
        public byte Byte() => _p < _d.Length ? _d[_p++] : throw new EndOfStreamException();
        public ushort U16() => (ushort)(Byte() | (Byte() << 8));
        public uint U32() => (uint)(Byte() | (Byte() << 8) | (Byte() << 16) | (Byte() << 24));
        public int I32() => unchecked((int)U32());
        public void Skip(int n) { if (n < 0 || _p + n > _d.Length) throw new EndOfStreamException(); _p += n; }

        public string Str()
        {
            // OpenRGB strings: u16 length INCLUDES the trailing NUL; bytes are NUL-terminated.
            ushort len = U16();
            if (len == 0) return "";
            if (_p + len > _d.Length) throw new EndOfStreamException();
            var s = System.Text.Encoding.UTF8.GetString(_d, _p, len - 1);
            _p += len;
            return s;
        }
    }
}
