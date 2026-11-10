using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.ExceptionServices;

namespace DiskRescue.Desktop;

public partial class App : Application
{
    private static string? CaptureDiagnosticPath =>
        Environment.GetEnvironmentVariable("DISKRESCUE_CAPTURE_UI_SCREENSHOT");

    static App()
    {
        // Install diagnostics before WPF creates the Application instance. First-chance
        // logging catches XAML/resource failures that may occur before OnStartup.
        if (string.IsNullOrWhiteSpace(CaptureDiagnosticPath))
            return;

        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            try
            {
                File.AppendAllText(CaptureDiagnosticPath + ".trace.txt",
                    $"FIRST-CHANCE {DateTimeOffset.UtcNow:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}---{Environment.NewLine}");
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(CaptureDiagnosticPath + ".error.txt",
                    $"UNHANDLED {DateTimeOffset.UtcNow:O}{Environment.NewLine}{args.ExceptionObject}{Environment.NewLine}");
            }
            catch { }
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                File.AppendAllText(CaptureDiagnosticPath + ".trace.txt",
                    $"PROCESS EXIT {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            }
            catch { }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var diagnosticPath = CaptureDiagnosticPath;
        if (!string.IsNullOrWhiteSpace(diagnosticPath))
        {
            DispatcherUnhandledException += (_, args) =>
            {
                try
                {
                    File.AppendAllText(diagnosticPath + ".error.txt",
                        $"DISPATCHER UNHANDLED {DateTimeOffset.UtcNow:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}");
                }
                catch { }
                args.Handled = false;
            };
        }

        base.OnStartup(e);

        var screenshotPath = CaptureDiagnosticPath;
        if (string.IsNullOrWhiteSpace(screenshotPath))
            return;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var discoveryImagePath = Environment.GetEnvironmentVariable("DISKRESCUE_WORKSPACE_SCREENSHOT_IMAGE");
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
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
                    await mainWindow.CaptureWorkspaceScenarioAsync(discoveryImagePath);
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
