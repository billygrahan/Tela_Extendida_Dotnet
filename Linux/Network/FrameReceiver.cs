using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace Linux.Network;

public class FrameReceiver
{
    private unsafe AVCodecContext* _decoderContext;
    private unsafe AVFrame* _frame;
    private unsafe AVPacket* _packet;
    private unsafe SwsContext* _swsContext;

    public event Action<int, int, int, int, byte[]>? OnFrameUnpacked;

    // Mapeamento direto da função nativa da biblioteca libswscale
    [DllImport("libswscale", CallingConvention = CallingConvention.Cdecl)]
    private static unsafe extern int sws_scale(
        void* c,
        byte** srcSlice,
        int* srcStride,
        int srcSliceY,
        int srcSliceH,
        byte** dst,
        int* dstStride
    );

    private unsafe void InitDecoder()
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);

        _decoderContext = ffmpeg.avcodec_alloc_context3(codec);
        _decoderContext->thread_count = 2;
        ffmpeg.avcodec_open2(_decoderContext, codec, null);

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
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
            client.SendBufferSize = 1024 * 1024;
            client.ReceiveBufferSize = 1024 * 1024;

            using var stream = client.GetStream();
            byte[] lengthBuffer = new byte[4];

            while (client.Connected)
            {
                if (!await ReadExactAsync(stream, lengthBuffer, 0, 4))
                {
                    Console.WriteLine("[FrameReceiver] Desconectado: falha ao ler tamanho do pacote.");
                    break;
                }

                int packetSize = BitConverter.ToInt32(lengthBuffer, 0);
                if (packetSize <= 0) continue;

                byte[] h264Bytes = new byte[packetSize];
                if (!await ReadExactAsync(stream, h264Bytes, 0, packetSize))
                {
                    Console.WriteLine("[FrameReceiver] Desconectado: falha ao ler payload de vídeo.");
                    break;
                }

                ProcessH264Packet(h264Bytes, packetSize);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FrameReceiver] ERRO crítico no fluxo de vídeo: {ex.Message}");
        }
    }

    private unsafe void ProcessH264Packet(byte[] h264Bytes, int packetSize)
    {
        fixed (byte* pData = h264Bytes)
        {
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
            byte*[] dstDataArr = new byte*[] { ptrBgra, null, null, null };
            int[] dstLinesizeArr = new int[] { width * 4, 0, 0, 0 };

            fixed (byte** pDstData = dstDataArr)
            {
                fixed (int* pDstLinesize = dstLinesizeArr)
                {
                    byte** pSrcData = (byte**)&(frame->data);
                    int* pSrcLinesize = (int*)&(frame->linesize);

                    sws_scale(
                        _swsContext,
                        pSrcData,
                        pSrcLinesize,
                        0,
                        height,
                        pDstData,
                        pDstLinesize
                    );
                }
            }
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