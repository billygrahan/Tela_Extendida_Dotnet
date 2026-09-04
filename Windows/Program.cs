using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Windows.Capture;
using Windows.Network;

namespace Windows;

class Program
{
    static void Main(string[] args)
    {

        Console.WriteLine("=== ScreenExtender Server (Windows) ===");

        // 1. Inicia o transmissor UDP de anúncios de rede
        var broadcaster = new DiscoveryBroadcaster();
        broadcaster.Start();

        // 2. Inicia o servidor TCP de streaming de telas
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
            exitEvent.Set();
        };

        exitEvent.WaitOne();
        Console.WriteLine("Servidor finalizado com sucesso.");
    }
}