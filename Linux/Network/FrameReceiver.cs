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
    
    // Action: x, y, width, height, rawPixelData
    public event Action<int, int, int, int, byte[]>? OnFrameUnpacked;

    public async Task ConnectAndReceiveAsync(IPAddress serverIp)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(serverIp, 45679);

        client.NoDelay = true;
        client.SendBufferSize = 1024 * 1024;
        client.ReceiveBufferSize = 1024 * 1024;

        using var stream = client.GetStream();
        byte[] lengthBuffer = new byte[4];

        while (client.Connected)
        {
            // 1. Lê exatamente 4 bytes do cabeçalho com o tamanho do pacote
            if (!await ReadExactAsync(stream, lengthBuffer, 0, 4)) break;

            int packetSize = BitConverter.ToInt32(lengthBuffer, 0);
            if (packetSize <= 0) continue;

            byte[] packetBytes = new byte[packetSize];

            // 2. Lê exatamente o tamanho total do pacote comprimido
            if (!await ReadExactAsync(stream, packetBytes, 0, packetSize)) break;

            // 3. Deserializa e descompacta o frame
            using var ms = new MemoryStream(packetBytes);
            using var reader = new BinaryReader(ms);

            var frameHeader = FrameHeader.Deserialize(reader);
            if (frameHeader.RectCount > 0)
            {
                var rectHeader = RectHeader.Deserialize(reader);
                byte[] compressedData = reader.ReadBytes(rectHeader.CompressedDataSize);

                byte[] rawPixels = _decompressor.Unwrap(compressedData).ToArray();

                // Dispara o evento para atualizar a UI
                OnFrameUnpacked?.Invoke(rectHeader.X, rectHeader.Y, rectHeader.Width, rectHeader.Height, rawPixels);
            }
        }
    }

    // Método auxiliar essencial para evitar leitura parcial na rede TCP
    private async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead);
            if (read == 0) return false; // Conexão encerrada
            totalRead += read;
        }
        return true;
    }
}