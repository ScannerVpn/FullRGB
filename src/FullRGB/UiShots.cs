using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace FullRGB;

/// <summary>
/// --uishot: renders the windows to PNG files without hardware, so the layout can be
/// reviewed (and regressions spotted) without launching the full app.
/// </summary>
public static class UiShots
{
    public static int Run(string[] args)
    {
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                         ?.Split('=', 2)[1]
                     ?? Path.Combine(Path.GetTempPath(), "fullrgb-shots");
        Directory.CreateDirectory(outDir);

        try
        {
            var win = new MainWindow();
            Shoot(win, 560, 700, Path.Combine(outDir, "main-lighting.png"));

            win.ShowDevicesTabForShot();
            Shoot(win, 560, 700, Path.Combine(outDir, "main-devices.png"));

            win.ShowHardwareTabForShot();
            Shoot(win, 560, 700, Path.Combine(outDir, "main-hardware.png"));

            win.ShowSettingsTabForShot();
            Shoot(win, 560, 700, Path.Combine(outDir, "main-settings.png"));
            win.Close();

            var startup = new StartupWindow();
            Shoot(startup, 460, 392, Path.Combine(outDir, "startup.png"));
            startup.Close();

            var picker = new ColorPickerDialog(null, Color.FromRgb(0, 229, 255));
            Shoot(picker, 288, 470, Path.Combine(outDir, "colorpicker.png"));
            picker.Close();

            Console.WriteLine("SHOTS_DIR=" + outDir);
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("uishot failed: " + e);
            return 1;
        }
    }

    private static void Shoot(Window w, int width, int height, string path)
    {
        w.Width = width;
        // Dialogs that size to content must keep doing so, or the shot invents dead space
        // that the real window does not have.
        if (w.SizeToContent is SizeToContent.Manual or SizeToContent.Width) w.Height = height;
        // A Window only builds its visual tree once it has a presentation source; showing it
        // off-screen is the reliable way to get a real render without stealing focus.
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000;
        w.Top = -10000;
        w.ShowActivated = false;
        w.Show();
        w.UpdateLayout();
        // let the dispatcher run pending layout/render work
        for (int i = 0; i < 4; i++)
            w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        var content = (FrameworkElement)w.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        // Render the WINDOW visual, not the content element: the content's own Margin lives
        // outside its visual, so capturing the child alone lost the window padding and made
        // text look clipped against the left edge.
        int cw = (int)Math.Ceiling(Math.Max(1, w.ActualWidth));
        int chh = (int)Math.Ceiling(Math.Max(1, w.ActualHeight));

        var bmp = new RenderTargetBitmap(cw, chh, 96, 96, PixelFormats.Pbgra32);
        // RenderTargetBitmap starts transparent and a Window's Background is not part of its
        // child visual, so paint the backdrop first, then composite the window on top.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(w.Background ?? Brushes.Black, null, new Rect(0, 0, cw, chh));
        bmp.Render(dv);
        bmp.Render(w);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        Console.WriteLine("wrote " + path);
        w.Hide();
    }
}
