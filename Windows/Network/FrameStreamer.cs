using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Windows.Capture;

namespace Windows.Network;

public class FrameStreamer
{
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public FrameStreamer(int port = 45679)
    {
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        Console.WriteLine($"[FrameStreamer] Aguardando conexões TCP na porta {_port}...");

        // Inicia a escuta contínua em segundo plano
        _listenTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token));
    }

    private async Task AcceptClientsLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                // Aguarda o próximo cliente TCP se conectar
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                var clientEndPoint = client.Client.RemoteEndPoint?.ToString();

                Console.WriteLine($"[FrameStreamer] Cliente conectado: {clientEndPoint}");

                using (var networkStream = client.GetStream())
                {
                    // 1. O 'using' aqui garante o Dispose() do DXGI no encerramento da conexão!
                    using (var capturer = new DxgiCapturer())
                    {
                        // Transmite os frames enquanto a conexão estiver ativa
                        await capturer.StartCaptureAndStreamAsync(networkStream, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelamento normal solicitado pelo Stop()
                break;
            }
            catch (Exception ex)
            {
                // Captura qualquer erro de rede ou desconexão abrupta do cliente
                Console.WriteLine($"[FrameStreamer] Cliente desconectado/erro: {ex.Message}");
            }
            finally
            {
                // 2. Garante o fechamento limpo do socket do cliente
                if (client != null)
                {
                    client.Close();
                    client.Dispose();
                }

                // 3. Limpa a memória nativa da GPU e prepara o ambiente para o próximo cliente
                GC.Collect();
                GC.WaitForPendingFinalizers();

                Console.WriteLine("[FrameStreamer] Sessão finalizada. Aguardando novo cliente...");
            }

            // Delay preventivo para evitar sobrecarga antes da próxima escuta
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        Console.WriteLine("[FrameStreamer] Servidor TCP interrompido.");
    }
}