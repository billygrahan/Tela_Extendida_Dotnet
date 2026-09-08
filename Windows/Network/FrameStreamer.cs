using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shared;
using Windows.Capture;

namespace Windows.Network;

public class FrameStreamer
{
    private TcpListener? _listener;
    private bool _isRunning;

    public void Start()
    {
        _isRunning = true;

        Task.Run(async () =>
        {
            try
            {
                // Escuta em todas as interfaces de rede na porta definida
                _listener = new TcpListener(IPAddress.Any, StreamSettings.StreamPort);
                _listener.Start();
                Console.WriteLine($"[FrameStreamer] Aguardando conexões TCP na porta {StreamSettings.StreamPort}...");

                while (_isRunning)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    Console.WriteLine($"[FrameStreamer] Cliente conectado: {client.Client.RemoteEndPoint}");

                    // Ao conectar, inicia a captura e envia pela NetworkStream do cliente
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var stream = client.GetStream();
                            var capturer = new DxgiCapturer();
                            await capturer.StartCaptureAndStreamAsync(stream);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[FrameStreamer Error] Conexão encerrada: {ex}");
                        }
                        finally
                        {
                            client.Close();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FrameStreamer Error] {ex}");
            }
        });
    }

    public void Stop()
    {
        _isRunning = false;
        _listener?.Stop();
    }
}