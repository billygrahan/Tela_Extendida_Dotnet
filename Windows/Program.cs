using System;
using System.IO;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen;
using Windows.Network;

namespace Windows;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== ScreenExtender Server (Windows) ===");

        string ffmpegRoot = Environment.GetEnvironmentVariable("FFMPEG_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        ffmpeg.RootPath = ffmpegRoot;
        Console.WriteLine($"[FFmpeg] Procurando DLLs em: {ffmpegRoot}");
        ValidateFfmpegFiles(ffmpegRoot);

        // 1. Inicia o transmissor UDP de anúncios de rede
        var broadcaster = new DiscoveryBroadcaster();
        broadcaster.Start();

        // 2. Inicia o servidor TCP que aceita clientes e faz o streaming DXGI
        var streamer = new FrameStreamer();
        streamer.Start();

        Console.WriteLine("\n[Servidor Ativo e Aguardando Conexões]");
        Console.WriteLine("Pressione CTRL+C no terminal do Windows para encerrar.\n");

        // 3. Bloqueia a thread principal para manter o servidor aberto
        var exitEvent = new ManualResetEvent(false);
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            Console.WriteLine("\nEncerrando o servidor...");
            eventArgs.Cancel = true;
            broadcaster.Stop();
            streamer.Stop(); // Opcional: adicionar método Stop no FrameStreamer
            exitEvent.Set();
        };

        exitEvent.WaitOne();
        Console.WriteLine("Servidor finalizado com sucesso.");
    }

    private static void ValidateFfmpegFiles(string root)
    {
        string[] requiredFiles = { "avcodec-60.dll", "avutil-58.dll", "swscale-7.dll" };
        string[] missingFiles = requiredFiles
            .Where(file => !File.Exists(Path.Combine(root, file)))
            .ToArray();

        if (missingFiles.Length > 0)
        {
            throw new FileNotFoundException(
                $"As DLLs do FFmpeg 6.1 não foram encontradas em '{root}'. " +
                $"Ausentes: {string.Join(", ", missingFiles)}. " +
                "Não use DLLs avcodec-61/62 com FFmpeg.AutoGen 6.1.0.1.",
                root);
        }

        foreach (string file in requiredFiles)
        {
            Console.WriteLine($"[FFmpeg] DLL encontrada: {file}");
        }
    }
}