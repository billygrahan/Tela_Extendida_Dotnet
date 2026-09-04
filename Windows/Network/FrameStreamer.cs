using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shared;
using Windows.Capture;

namespace Windows.Network;

public class FrameStreamer
{
    private TcpListener? _listener;

    public async Task Start()
    {
        _listener = new TcpListener(IPAddress.Any, Constants.StreamPort);
        _listener.Start();

        Console.WriteLine($"[FrameStreamer] Escutando na porta TCP {Constants.StreamPort}...");

        while (true)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();

                // Desativa o algoritmo de Nagle e aumenta os buffers para zerar a latência do streaming
                client.NoDelay = true;
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;

                Console.WriteLine($"\n[FrameStreamer] CLIENTE CONECTADO de {client.Client.RemoteEndPoint}!");

                using var stream = client.GetStream();
                var capturer = new DxgiCapturer();

                // Inicia a captura via DXGI e envia os frames pela stream TCP
                await capturer.StartCaptureAndStreamAsync(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FrameStreamer Error] Cliente desconectado ou erro: {ex.Message}");
            }
        }
    }
}