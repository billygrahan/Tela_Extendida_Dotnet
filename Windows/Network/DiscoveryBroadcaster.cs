using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Windows.Network;

public class DiscoveryBroadcaster
{
    private readonly UdpClient _udpClient = new();
    private CancellationTokenSource? _cts;

    public void Start()
    {
        _udpClient.EnableBroadcast = true;
        _cts = new CancellationTokenSource();

        var endPoint = new IPEndPoint(IPAddress.Broadcast, Constants.DiscoveryPort);
        var message = new DiscoveryMessage
        {
            ServerName = Environment.MachineName
        }.Serialize();

        Task.Run(async () =>
        {
            Console.WriteLine($"[Broadcaster] Anunciando servidor na porta UDP {Constants.DiscoveryPort}...");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await _udpClient.SendAsync(message, message.Length, endPoint);
                    await Task.Delay(2000, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Broadcaster Error] {ex.Message}");
                }
            }
        }, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient.Close();
    }
}