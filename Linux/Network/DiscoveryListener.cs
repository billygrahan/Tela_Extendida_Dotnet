using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Linux.Network;

public class DiscoveryListener
{
    private UdpClient? _udpClient;

    public async Task<IPAddress?> ListenForServerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, Constants.DiscoveryPort));

            Console.WriteLine($"[Listener] Aguardando broadcast UDP na porta {Constants.DiscoveryPort}...");

            using var ctsTimeout = new CancellationTokenSource(3000); // Aguarda até 3s pelo UDP
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctsTimeout.Token);

            var result = await _udpClient.ReceiveAsync(linkedCts.Token);
            var message = DiscoveryMessage.Deserialize(result.Buffer);

            if (message != null)
            {
                Console.WriteLine($"[Listener] Servidor encontrado via UDP em {result.RemoteEndPoint.Address}!");
                return result.RemoteEndPoint.Address;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Listener] Broadcast UDP não detectado no cabo a cabo. Iniciando detecção do IP no cabo...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Listener Warning] {ex.Message}");
        }
        finally
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }

        // --- FALLBACK DIRETO PARA CABO PONTO A PONTO ---
        return DiscoverDirectCableIp();
    }

    private IPAddress? DiscoverDirectCableIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Name == "enp2s0" && nic.OperationalStatus == OperationalStatus.Up)
            {
                var props = nic.GetIPProperties();
                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] bytes = ip.Address.GetAddressBytes();
                        // Se for um IP APIPA/Link-Local (169.254.x.x)
                        if (bytes[0] == 169 && bytes[1] == 254)
                        {
                            // Tenta conectar no IP atribuído à Ethernet 2 do Windows
                            // (obtido do seu ipconfig: 169.254.139.229)
                            var targetIp = IPAddress.Parse("169.254.139.229");
                            Console.WriteLine($"[Fallback Cabo] Conectando diretamente ao IP do Windows na Ethernet 2: {targetIp}");
                            return targetIp;
                        }
                    }
                }
            }
        }

        return null;
    }
}