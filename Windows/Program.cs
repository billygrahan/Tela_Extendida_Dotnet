using System;
using Windows.Network;

namespace Windows;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== ScreenExtender Server (Windows) ===");

        var broadcaster = new DiscoveryBroadcaster();
        broadcaster.Start();

        var streamer = new FrameStreamer();
        streamer.Start();

        Console.WriteLine("\nServidor pronto. Inicie o cliente no Linux e pressione qualquer tecla para enviar um frame de teste...");
        Console.ReadKey();

        // Envia uma região de teste de 100x100 com pixels fake
        byte[] fakePixels = new byte[100 * 100 * 4];
        Array.Fill<byte>(fakePixels, 255); // Preenche buffer simulando pixels RGBA

        streamer.SendDirtyRect(0, 0, 100, 100, fakePixels);

        Console.WriteLine("Frame enviado! Verifique o console do Linux.");
        Console.ReadLine();
    }
}