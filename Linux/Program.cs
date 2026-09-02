using System;
using System.Threading.Tasks;
using Linux.Network;

namespace Linux;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== ScreenExtender Client (Linux) - Teste de Descoberta ===");

        var listener = new DiscoveryListener();
        var serverIp = await listener.ListenForServerAsync();

        if (serverIp != null)
        {
            Console.WriteLine($"\n[Sucesso] Conexão pronta para ser iniciada com {serverIp}!");
        }
        else
        {
            Console.WriteLine("\n[Falha] Nenhum servidor foi encontrado na rede.");
        }
    }
}