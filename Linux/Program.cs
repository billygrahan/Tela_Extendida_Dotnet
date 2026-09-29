using System;
using System.IO;
using System.Linq;
using Avalonia;
using FFmpeg.AutoGen;

namespace Linux;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string packagedRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native");
        string ffmpegRoot = Environment.GetEnvironmentVariable("FFMPEG_ROOT")
            ?? (HasFfmpegFiles(packagedRoot) ? packagedRoot : FindFfmpegRoot());

        ffmpeg.RootPath = ffmpegRoot;
        Console.WriteLine($"[FFmpeg] Procurando bibliotecas em: {ffmpegRoot}");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static string FindFfmpegRoot()
    {
        string localFfmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

        string[] candidates =
        {
            localFfmpegPath,
            AppContext.BaseDirectory,
            "/usr/local/lib",
            "/usr/lib",
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib/aarch64-linux-gnu",
            "/lib/x86_64-linux-gnu",
            "/lib/aarch64-linux-gnu"
        };

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "libavcodec.so*").Any())
            {
                return candidate;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static bool HasFfmpegFiles(string root)
    {
        string[] requiredFiles = { "libavcodec.so.60", "libavutil.so.58", "libswscale.so.7" };
        return requiredFiles.All(file => File.Exists(Path.Combine(root, file)));
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                UseDBusMenu = false
            })
            .LogToTrace();
}