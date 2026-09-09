// Minimal stub of the renderer context so _probe projects can compile SensorProviders.cs
// (which references FullRGB.Effects.EffectContext in FillScreenContext) without the whole
// Effects surface. The REAL app always compiles the real Effects.cs; this stub exists only
// for the standalone audio probe.
namespace FullRGB.Effects;

public sealed class EffectContext
{
    public double Time;
    public double? CpuTemp;
    public double? GpuTemp;
    public double AudioLevel;
    public double AudioBass, AudioMid, AudioTreble;
    public double Beat;
    public double ScreenAvgR, ScreenAvgG, ScreenAvgB;
    public double ScreenRow0R, ScreenRow0G, ScreenRow0B;
    public double ScreenRow1R, ScreenRow1G, ScreenRow1B;
    public double ScreenRow2R, ScreenRow2G, ScreenRow2B;
    public bool ScreenValid;
}
