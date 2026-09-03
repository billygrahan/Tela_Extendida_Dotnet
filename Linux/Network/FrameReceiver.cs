using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shared;
using ZstdSharp;

namespace Linux.Network;

public class FrameReceiver
{
    private readonly Decompressor _decompressor = new();

    public async Task ConnectAndReceiveAsync(IPAddress serverIp)
    {
        using var client = new TcpClient();
        client.NoDelay = true;

        Console.WriteLine($"[FrameReceiver] Conectando ao servidor TCP {serverIp}:{Constants.StreamPort}...");
        await client.ConnectAsync(serverIp, Constants.StreamPort);

        using var stream = client.GetStream();
        using var reader = new BinaryReader(stream);

        Console.WriteLine("[FrameReceiver] Conectado! Recebendo fluxo de dados de vídeo...\n");

        while (client.Connected)
        {
            // 1. Lê o tamanho do próximo pacote
            byte[] sizeBuffer = new byte[4];
            int bytesRead = await stream.ReadAsync(sizeBuffer, 0, 4);
            if (bytesRead < 4) break;

            int packetSize = BitConverter.ToInt32(sizeBuffer, 0);
            byte[] packetBuffer = new byte[packetSize];

            // 2. Garante a leitura completa do pacote
            int totalRead = 0;
            while (totalRead < packetSize)
            {
                int read = await stream.ReadAsync(packetBuffer, totalRead, packetSize - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            // 3. Processa o pacote
            using var ms = new MemoryStream(packetBuffer);
            using var packetReader = new BinaryReader(ms);

            var frameHeader = FrameHeader.Deserialize(packetReader);

            for (int i = 0; i < frameHeader.RectCount; i++)
            {
                var rectHeader = RectHeader.Deserialize(packetReader);
                byte[] compressedBytes = packetReader.ReadBytes(rectHeader.CompressedDataSize);

                // Descomprime os dados Zstd em memória
                ReadOnlySpan<byte> decompressedPixels = _decompressor.Unwrap(compressedBytes);

                Console.WriteLine($"[Frame Recebido] Região: {rectHeader.Width}x{rectHeader.Height} em ({rectHeader.X},{rectHeader.Y}) | Tamanho comprimido: {rectHeader.CompressedDataSize} bytes | Pixels: {decompressedPixels.Length} bytes");
            }
        }
    }
}