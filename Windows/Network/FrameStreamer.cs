using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shared;
using ZstdSharp;

namespace Windows.Network;

public class FrameStreamer
{
    private readonly TcpListener _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly Compressor _compressor = new(level: 3); // Nível de compressão ideal para baixa latência

    public FrameStreamer()
    {
        _listener = new TcpListener(IPAddress.Any, Constants.StreamPort);
    }

    public void Start()
    {
        _listener.Start();
        Console.WriteLine($"[FrameStreamer] Aguardando conexão TCP do cliente na porta {Constants.StreamPort}...");

        Task.Run(async () =>
        {
            _client = await _listener.AcceptTcpClientAsync();
            _client.NoDelay = true; // Desabilita o algoritmo de Nagle para latência mínima
            _stream = _client.GetStream();
            Console.WriteLine($"[FrameStreamer] Cliente conectado de: {_client.Client.RemoteEndPoint}");
        });
    }

    public bool IsConnected => _client is { Connected: true } && _stream != null;

    public void SendDirtyRect(int x, int y, int width, int height, byte[] rawPixelData)
    {
        if (!IsConnected || _stream == null) return;

        try
        {
            // Comprime os pixels da região usando Zstd
            ReadOnlySpan<byte> compressedData = _compressor.Wrap(rawPixelData);

            using var memoryStream = new MemoryStream();
            using var writer = new BinaryWriter(memoryStream);

            // Escreve os cabeçalhos e os dados comprimidos
            var frameHeader = new FrameHeader { RectCount = 1 };
            frameHeader.Serialize(writer);

            var rectHeader = new RectHeader
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                CompressedDataSize = compressedData.Length
            };
            rectHeader.Serialize(writer);
            writer.Write(compressedData);

            byte[] packet = memoryStream.ToArray();

            // Manda o tamanho do pacote total seguido pelo payload
            byte[] sizeHeader = BitConverter.GetBytes(packet.Length);
            _stream.Write(sizeHeader, 0, 4);
            _stream.Write(packet, 0, packet.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Streamer Error] Falha ao enviar frame: {ex.Message}");
        }
    }
}