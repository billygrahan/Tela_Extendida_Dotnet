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
            ?? (Directory.Exists(packagedRoot) ? packagedRoot : FindFfmpegRoot());
        ffmpeg.RootPath = ffmpegRoot;
        Console.WriteLine($"[FFmpeg] Procurando bibliotecas em: {ffmpegRoot}");
        ValidateFfmpegFiles(ffmpegRoot);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ValidateFfmpegFiles(string root)
    {
        string[] requiredFiles = { "libavcodec.so.60", "libavutil.so.58", "libswscale.so.7" };
        string[] missingFiles = requiredFiles
            .Where(file => !File.Exists(Path.Combine(root, file)))
            .ToArray();

        if (missingFiles.Length > 0)
        {
            throw new FileNotFoundException(
                $"As bibliotecas do FFmpeg 6.1 não foram encontradas em '{root}'. " +
                $"Ausentes: {string.Join(", ", missingFiles)}. " +
                "Não use bibliotecas libavcodec.so.61/62 com FFmpeg.AutoGen 6.1.0.1.",
                root);
        }
    }

    private static string FindFfmpegRoot()
    {
        string[] candidates =
        {
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}