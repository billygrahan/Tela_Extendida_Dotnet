using System;
using System.IO;
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
        string[] codecFiles = Directory.Exists(root)
            ? Directory.GetFiles(root, "avcodec*.dll")
            : Array.Empty<string>();

        if (codecFiles.Length == 0)
        {
            throw new FileNotFoundException(
                $"Nenhuma DLL avcodec*.dll foi encontrada em '{root}'. " +
                "A instalação precisa ser uma build shared do FFmpeg com as DLLs nativas.",
                root);
        }

        foreach (string codecFile in codecFiles)
        {
            Console.WriteLine($"[FFmpeg] DLL encontrada: {Path.GetFileName(codecFile)}");
        }
    }
}