using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Shared;

namespace Linux.Network;

public class FrameReceiver
{
    private unsafe AVCodecContext* _decoderContext;
    private unsafe AVFrame* _frame;
    private unsafe AVPacket* _packet;
    private unsafe SwsContext* _swsContext;

    // Adicionamos as propriedades do cursor no formato do JSON enviado
    public class CursorState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool Visible { get; set; }
        public int Type { get; set; }
    }

    public event Action<int, int, int, int, byte[]>? OnFrameUnpacked;
    
    // Novo evento para avisar a UI do Linux que o mouse moveu
    public event Action<CursorState>? OnCursorMoved; 

    private unsafe void InitDecoder()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);

        if (codec == null)
        {
            throw new InvalidOperationException("Nenhum decoder H.264 disponível no FFmpeg.");
        }

        _decoderContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_decoderContext == null)
        {
            throw new InvalidOperationException("Não foi possível alocar o contexto do decoder H.264.");
        }

        _decoderContext->thread_count = StreamSettings.DecoderThreadCount;
        int openResult = ffmpeg.avcodec_open2(_decoderContext, codec, null);
        if (openResult < 0)
        {
            throw new InvalidOperationException($"Não foi possível abrir o decoder H.264: {openResult}.");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame == null || _packet == null)
        {
            throw new InvalidOperationException("Não foi possível alocar estruturas do decoder H.264.");
        }
    }

    public async Task ConnectAndReceiveAsync(System.Net.IPAddress serverIp)
    {
        try
        {
            InitDecoder();

            using var client = new TcpClient();
            Console.WriteLine($"[FrameReceiver] Conectando via TCP ao servidor {serverIp}:45679...");
            
            await client.ConnectAsync(serverIp, 45679);
            Console.WriteLine("[FrameReceiver] Conexão TCP estabelecida com sucesso!");

            client.NoDelay = true;
            client.SendBufferSize = StreamSettings.SocketBufferSize;
            client.ReceiveBufferSize = StreamSettings.SocketBufferSize;

            using var stream = client.GetStream();
            
            // Buffers para o novo protocolo: 1 byte Tipo, 4 bytes Tamanho
            byte[] typeBuffer = new byte[1];
            byte[] lengthBuffer = new byte[4];

            while (client.Connected)
            {
                // 1. Lê o TIPO do pacote (1 byte)
                if (!await ReadExactAsync(stream, typeBuffer, 0, 1))
                {
                    Console.WriteLine("[FrameReceiver] Desconectado: falha ao ler tipo de pacote.");
                    break;
                }
                byte packetType = typeBuffer[0];

                // 2. Lê o TAMANHO do pacote (4 bytes)
                if (!await ReadExactAsync(stream, lengthBuffer, 0, 4))
                {
                    Console.WriteLine("[FrameReceiver] Desconectado: falha ao ler tamanho do pacote.");
                    break;
                }
                int packetSize = BitConverter.ToInt32(lengthBuffer, 0);
                if (packetSize <= 0) continue;

                // 3. Lê o PAYLOAD completo
                byte[] payloadBytes = new byte[packetSize];
                if (!await ReadExactAsync(stream, payloadBytes, 0, packetSize))
                {
                    Console.WriteLine("[FrameReceiver] Desconectado: falha ao ler payload.");
                    break;
                }

                // 4. Roteamento baseado no TIPO
                if (packetType == 0) // PacketType.Video
                {
                    ProcessH264Packet(payloadBytes, packetSize);
                }
                else if (packetType == 1) // PacketType.Cursor
                {
                    ProcessCursorPacket(payloadBytes);
                }
                else
                {
                    Console.WriteLine($"[FrameReceiver] Tipo de pacote desconhecido ignorado: {packetType}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FrameReceiver] ERRO crítico no fluxo de rede: {ex}");
        }
    }

    // Novo método para processar os dados JSON do cursor
    private void ProcessCursorPacket(byte[] payloadBytes)
    {
        try
        {
            string json = System.Text.Encoding.UTF8.GetString(payloadBytes);
            var cursorState = JsonSerializer.Deserialize<CursorState>(json);
            
            if (cursorState != null)
            {
                OnCursorMoved?.Invoke(cursorState);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FrameReceiver] Falha ao ler pacote de cursor: {ex.Message}");
        }
    }

    private unsafe void ProcessH264Packet(byte[] h264Bytes, int packetSize)
    {
        fixed (byte* pData = h264Bytes)
        {
            ffmpeg.av_packet_unref(_packet);
            _packet->data = pData;
            _packet->size = packetSize;

            if (ffmpeg.avcodec_send_packet(_decoderContext, _packet) >= 0)
            {
                while (ffmpeg.avcodec_receive_frame(_decoderContext, _frame) >= 0)
                {
                    int width = _frame->width;
                    int height = _frame->height;

                    if (width <= 0 || height <= 0) continue;

                    // Decodifica YUV para BGRA
                    byte[] rawBgra = DecodeFrameToBgra(_frame, width, height);

                    // Dispara o evento de frame pronto com os bytes convertidos
                    OnFrameUnpacked?.Invoke(0, 0, width, height, rawBgra);
                }
            }
        }
    }

    private unsafe byte[] DecodeFrameToBgra(AVFrame* frame, int width, int height)
    {
        byte[] bgraBuffer = new byte[width * height * 4];

        AVPixelFormat srcFormat = (AVPixelFormat)frame->format;
        if (srcFormat == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            srcFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        _swsContext = ffmpeg.sws_getCachedContext(
            _swsContext,
            width, height, srcFormat,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            1, // Flag 1 = SWS_FAST_BILINEAR
            null, null, null
        );

        fixed (byte* ptrBgra = bgraBuffer)
        {
            byte*[] dstData = new byte*[] { ptrBgra, null, null, null };
            int[] dstLinesize = new int[] { width * 4, 0, 0, 0 };

            byte*[] srcData = new byte*[4];
            int[] srcLinesize = new int[4];

            for (uint i = 0; i < 4; i++)
            {
                srcData[i] = frame->data[i];
                srcLinesize[i] = frame->linesize[i];
            }

            ffmpeg.sws_scale(
                _swsContext,
                srcData,
                srcLinesize,
                0,
                height,
                dstData,
                dstLinesize
            );
        }

        return bgraBuffer;
    }

    private async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead);
            if (read == 0) return false;
            totalRead += read;
        }
        return true;
    }
}