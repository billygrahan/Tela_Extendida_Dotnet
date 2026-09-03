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
        var serverIp = await listener.ListenForServerAsync();

        if (serverIp != null)
        {
            var receiver = new FrameReceiver();
            await receiver.ConnectAndReceiveAsync(serverIp);
        }
    }
}