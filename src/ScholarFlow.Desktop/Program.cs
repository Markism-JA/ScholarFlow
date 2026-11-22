using Avalonia;

namespace ScholarFlow.Desktop;

internal static class Program
{
    public static bool StartMinimized { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        StartMinimized = args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)
        );
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
}
