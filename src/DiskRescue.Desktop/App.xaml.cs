using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiskRescue.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var screenshotPath = Environment.GetEnvironmentVariable("DISKRESCUE_CAPTURE_UI_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(screenshotPath))
            return;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var window = MainWindow ?? Windows.OfType<Window>().FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "The DiskRescue main window was not created for screenshot capture.");
                if (!window.IsVisible)
                    window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();

                if (!string.IsNullOrWhiteSpace(discoveryImagePath))
                {
                    if (window is not MainWindow mainWindow)
                        throw new InvalidOperationException("Discovery screenshot requires the DiskRescue main window.");
                    await mainWindow.CaptureDiscoveryScenarioAsync(discoveryImagePath);
                }

                window.UpdateLayout();

                var dpi = VisualTreeHelper.GetDpi(window);
                var pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
                var pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
                var bitmap = new RenderTargetBitmap(
                    pixelWidth, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(window);

                var fullPath = Path.GetFullPath(screenshotPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new IOException("Screenshot destination directory could not be resolved.");
                Directory.CreateDirectory(directory);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(fullPath))
                    encoder.Save(output);

                Shutdown(0);
            }
            catch (Exception exception)
            {
                try
                {
                    File.WriteAllText(screenshotPath + ".error.txt", exception.ToString());
                }
                catch
                {
                    // Preserve the original screenshot-capture failure.
                }
                Console.Error.WriteLine("DiskRescue UI screenshot capture failed: " + exception);
                Shutdown(1);
            }
        }));
    }
}
