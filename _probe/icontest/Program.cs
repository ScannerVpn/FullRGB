// Does System.Drawing.Icon actually load our app.ico? PNG-compressed ICO frames
// are a known GDI+ failure mode, and a failed load is why the tray can stay blank.
using System.Drawing;

string path = @"G:\Ai\RGB Control\src\FullRGB\Assets\app.ico";

void Try(string label, Func<Icon> f)
{
    try
    {
        var ic = f();
        Console.WriteLine($"{label}: OK  size={ic.Width}x{ic.Height}");
        using var bmp = ic.ToBitmap();
        // count non-transparent pixels — a blank icon means a silently failed decode
        int solid = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 20) solid++;
        Console.WriteLine($"        visible pixels: {solid} / {bmp.Width * bmp.Height}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"{label}: FAILED {e.GetType().Name}: {e.Message}");
    }
}

Try("Icon(path)", () => new Icon(path));
Try("Icon(path, 16,16)", () => new Icon(path, 16, 16));
Try("Icon(path, 32,32)", () => new Icon(path, 32, 32));
Try("Icon(stream, 16,16)", () => { using var s = File.OpenRead(path); return new Icon(s, new Size(16, 16)); });
