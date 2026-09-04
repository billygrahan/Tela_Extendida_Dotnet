using System;
using System.Threading.Tasks;
using Linux.Network;

namespace Linux;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== ScreenExtender Client (Linux) ===");

        var listener = new DiscoveryListener();
        
        // Aguarda a descoberta automática do IP via broadcast UDP
        var serverIp = await listener.ListenForServerAsync();

        if (serverIp != null)
        {
            Console.WriteLine($"[Descoberta] Servidor identificado em {serverIp}. Iniciando recepção TCP...");
            var receiver = new FrameReceiver();
            await receiver.ConnectAndReceiveAsync(serverIp);
        }
        else
        {
            Console.WriteLine("[Erro] Nenhum servidor foi localizado na rede.");
        }
    }
}