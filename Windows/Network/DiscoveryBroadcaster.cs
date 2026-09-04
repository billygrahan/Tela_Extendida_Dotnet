using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Windows.Network;

public class DiscoveryBroadcaster
{
    private CancellationTokenSource? _cts;

    public void Start()
    {
        _cts = new CancellationTokenSource();

        var message = new DiscoveryMessage
        {
            ServerName = Environment.MachineName
        }.Serialize();

        Task.Run(async () =>
        {
            Console.WriteLine($"[Broadcaster] Anunciando servidor em todas as interfaces na porta UDP {Constants.DiscoveryPort}...");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up ||
                            nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                            continue;

                        var ipProps = nic.GetIPProperties();
                        foreach (var ip in ipProps.UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                try
                                {
                                    // 1. Calcula o endereço de broadcast específico da sub-rede desta interface
                                    byte[] ipBytes = ip.Address.GetAddressBytes();
                                    byte[] maskBytes = ip.IPv4Mask.GetAddressBytes();
                                    byte[] broadcastBytes = new byte[4];

                                    for (int i = 0; i < 4; i++)
                                    {
                                        broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                    }

                                    var directedBroadcast = new IPAddress(broadcastBytes);

                                    // 2. Dispara o broadcast direcionado pela interface específica
                                    using var client = new UdpClient(new IPEndPoint(ip.Address, 0));
                                    client.EnableBroadcast = true;

                                    // Envia tanto para o broadcast da sub-rede quanto para o global 255.255.255.255
                                    await client.SendAsync(message, message.Length, new IPEndPoint(directedBroadcast, Constants.DiscoveryPort));
                                    await client.SendAsync(message, message.Length, new IPEndPoint(IPAddress.Broadcast, Constants.DiscoveryPort));
                                }
                                catch
                                {
                                    // Ignora interfaces virtuais desconectadas
                                }
                            }
                        }
                    }

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
    }
}