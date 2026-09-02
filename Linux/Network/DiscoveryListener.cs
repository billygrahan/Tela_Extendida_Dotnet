using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Linux.Network;

public class DiscoveryListener
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    public async Task<IPAddress?> ListenForServerAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, Constants.DiscoveryPort));

            Console.WriteLine($"[Listener] Escutando broadcasts UDP na porta {Constants.DiscoveryPort}...");

            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                var message = DiscoveryMessage.Deserialize(result.Buffer);

                if (message != null)
                {
                    Console.WriteLine($"[Listener] Servidor encontrado!");
                    Console.WriteLine($" - Nome da Máquina: {message.ServerName}");
                    Console.WriteLine($" - Endereço IP: {result.RemoteEndPoint.Address}");
                    Console.WriteLine($" - Porta TCP do Stream: {message.TcpPort}");

                    return result.RemoteEndPoint.Address;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Listener] Busca cancelada.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Listener Error] {ex.Message}");
        }
        finally
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }

        return null;
    }

    public void Stop()
    {
        _cts?.Cancel();
    }
}