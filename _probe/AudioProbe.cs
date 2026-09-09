using FullRGB.Sensors;

var audio = new AudioProvider();
try { audio.Start(); }
catch (Exception e)
{
    Console.WriteLine($"AUDIO-START-FAILED: {e.Message}{(audio.LastError is null ? "" : " / " + audio.LastError)}");
    return;
}
if (audio.LastError is not null) Console.WriteLine($"WARN: {audio.LastError}");
Console.WriteLine("PROBE-START");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
var sw = System.Diagnostics.Stopwatch.StartNew();
double maxSeen = 0, maxBass = 0;
while (!cts.IsCancellationRequested)
{
    await Task.Delay(100);
    double lvl = audio.Level, bass = audio.Bass;
    if (lvl > maxSeen) maxSeen = lvl;
    if (bass > maxBass) maxBass = bass;
    Console.WriteLine($"t={sw.Elapsed.TotalSeconds:F1} level={lvl:F3} bass={bass:F3} beat={audio.Beat:F2} screen={(audio.ScreenSamples > 0 ? "ok" : "-")}");
}
Console.WriteLine($"PROBE-END maxLevel={maxSeen:F3} maxBass={maxBass:F3} screenSamples={audio.ScreenSamples}");
