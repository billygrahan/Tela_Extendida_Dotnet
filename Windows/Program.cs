using System;
using System.Threading;
using Windows.Network;

namespace Windows;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== ScreenExtender Server (Windows) ===");

        // 1. Inicializa o transmissor de descoberta UDP
        var broadcaster = new DiscoveryBroadcaster();
        broadcaster.Start();

        Console.WriteLine("\n[Servidor Rodando]");
        Console.WriteLine("Pressione CTRL+C para encerrar o servidor...\n");

        // 2. Trava a thread principal para o processo não fechar sozinho
        var exitEvent = new ManualResetEvent(false);
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            broadcaster.Stop();
            exitEvent.Set();
        };

        exitEvent.WaitOne();
        Console.WriteLine("Servidor finalizado.");
    }
}