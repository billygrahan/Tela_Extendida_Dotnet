using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Shared;
using Vortice.Direct3D11;
using Vortice.DXGI;

// Resolvendo ambiguidade entre Vortice e FFmpeg
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Windows.Capture;

public static class FFmpegHelper
{
    public static int CheckFFmpegError(this int error)
    {
        if (error < 0) throw new Exception($"Erro no FFmpeg: {error}");
        return error;
    }
}

public class DxgiCapturer
{
    private unsafe AVCodecContext* _codecContext;
    private unsafe AVFrame* _nv12Frame;
    private unsafe AVPacket* _packet;
    private unsafe SwsContext* _swsContext;

    // Buffer e estado do cursor retornado via DXGI
    private byte[]? _cursorShapeBuffer;
    private int _cursorWidth;
    private int _cursorHeight;
    private int _cursorPitch;
    private int _cursorX;
    private int _cursorY;
    private bool _cursorVisible;

    private unsafe void InitH264Encoder(int width, int height)
    {
        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);

        if (codec == null)
        {
            throw new InvalidOperationException("Nenhum encoder H.264 disponível no FFmpeg.");
        }

        string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "desconhecido";
        Console.WriteLine($"[FFmpeg] Encoder H.264 selecionado: {codecName}");

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
        {
            throw new InvalidOperationException("Não foi possível alocar o contexto do encoder H.264.");
        }

        _codecContext->width = width;
        _codecContext->height = height;
        _codecContext->time_base = new AVRational { num = 1, den = StreamSettings.TargetFps };
        _codecContext->framerate = new AVRational { num = StreamSettings.TargetFps, den = 1 };
        _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        _codecContext->bit_rate = StreamSettings.BitRate;
        _codecContext->gop_size = StreamSettings.KeyFrameInterval;
        _codecContext->max_b_frames = StreamSettings.MaxBFrames;

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "preset", StreamSettings.EncoderPreset, 0);
        ffmpeg.av_dict_set(&options, "tune", StreamSettings.EncoderTune, 0);
        ffmpeg.av_dict_set(&options, "profile", StreamSettings.EncoderProfile, 0);

        int openResult = ffmpeg.avcodec_open2(_codecContext, codec, &options);
        if (openResult < 0)
        {
            throw new InvalidOperationException($"Não foi possível abrir o encoder H.264 {codecName}: {openResult}.");
        }

        _nv12Frame = ffmpeg.av_frame_alloc();
        if (_nv12Frame == null)
        {
            throw new InvalidOperationException("Não foi possível alocar o frame NV12.");
        }

        _nv12Frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _nv12Frame->width = width;
        _nv12Frame->height = height;
        ffmpeg.av_frame_get_buffer(_nv12Frame, 32).CheckFFmpegError();

        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
        {
            throw new InvalidOperationException("Não foi possível alocar o pacote H.264.");
        }
    }

    public async Task StartCaptureAndStreamAsync(Stream networkStream, CancellationToken cancellationToken = default)
    {
        D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.None,
            null,
            out ID3D11Device? device).CheckError();

        using var dxgiDevice = device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        IDXGIOutput? targetOutput = null;
        for (uint outputIndex = 0; ; outputIndex++)
        {
            var outputResult = adapter.EnumOutputs(outputIndex, out var output);
            if (!outputResult.Success)
            {
                break;
            }

            var outputDescription = output.Description;
            Console.WriteLine(
                $"[DXGI] Output {outputIndex}: {outputDescription.DeviceName} " +
                $"({outputDescription.DesktopCoordinates.Left},{outputDescription.DesktopCoordinates.Top}) " +
                $"{outputDescription.DesktopCoordinates.Right - outputDescription.DesktopCoordinates.Left}x" +
                $"{outputDescription.DesktopCoordinates.Bottom - outputDescription.DesktopCoordinates.Top}");

            if (outputIndex == StreamSettings.TargetOutputIndex)
            {
                targetOutput = output;
            }
            else
            {
                output.Dispose();
            }
        }

        if (targetOutput == null)
        {
            throw new InvalidOperationException(
                $"O output DXGI de índice {StreamSettings.TargetOutputIndex} não foi encontrado.");
        }

        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        using var outputDuplication = output1.DuplicateOutput(device);

        bool encoderInitialized = false;

        var packetChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(StreamSettings.PacketChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            var reader = packetChannel.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var packet))
                {
                    byte[] sizeHeader = BitConverter.GetBytes(packet.Length);
                    await networkStream.WriteAsync(sizeHeader, 0, 4);
                    await networkStream.WriteAsync(packet, 0, packet.Length);
                }
            }
        });

        ID3D11Texture2D? stagingTexture = null;

        while (true)
        {
            // 1. Passamos 'out var frameInfo' para capturar metadados do cursor
            var result = outputDuplication.AcquireNextFrame(StreamSettings.AcquireNextFrameTimeoutMs, out var frameInfo, out var desktopResource);

            if (result.Success)
            {
                using (desktopResource)
                {
                    using var texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();
                    int width = (int)texture2D.Description.Width;
                    int height = (int)texture2D.Description.Height;

                    if (!encoderInitialized)
                    {
                        unsafe { InitH264Encoder(width, height); }
                        encoderInitialized = true;

                        var textureDesc = new Texture2DDescription
                        {
                            Width = (uint)width,
                            Height = (uint)height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            MiscFlags = ResourceOptionFlags.None
                        };

                        stagingTexture = device.CreateTexture2D(textureDesc);
                    }


                    if (frameInfo.PointerPosition.Visible)
                    {
                        _cursorVisible = true;
                        _cursorX = frameInfo.PointerPosition.Position.X;
                        _cursorY = frameInfo.PointerPosition.Position.Y;
                    }
                    //else
                    //{
                    //    _cursorVisible = false;
                    //}

                    if (frameInfo.PointerShapeBufferSize > 0)
                    {
                        _cursorShapeBuffer = new byte[frameInfo.PointerShapeBufferSize];
                        unsafe
                        {
                            fixed (byte* pShape = _cursorShapeBuffer)
                            {
                                outputDuplication.GetFramePointerShape(
                                    frameInfo.PointerShapeBufferSize,
                                    (IntPtr)pShape,
                                    out var requiredSize,
                                    out var shapeInfo);

                                // Corrigidos os casts implícitos de uint para int e o enum PointerShapeType
                                _cursorWidth = (int)shapeInfo.Width;
                                _cursorHeight = shapeInfo.Type == (uint)PointerShapeType.Monochrome
                                    ? (int)(shapeInfo.Height / 2)
                                    : (int)shapeInfo.Height;
                                _cursorPitch = (int)shapeInfo.Pitch;
                            }
                        }
                    }

                    device.ImmediateContext.CopyResource(stagingTexture!, texture2D);

                    byte[]? h264Packet = EncodeFrameToH264(device, stagingTexture!, width, height);
                    if (h264Packet != null)
                    {
                        packetChannel.Writer.TryWrite(h264Packet);
                    }
                }

                outputDuplication.ReleaseFrame();
            }
            else if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                await Task.Delay(1);
            }
        }
    }

    private unsafe byte[]? EncodeFrameToH264(ID3D11Device device, ID3D11Texture2D stagingTexture, int width, int height)
    {
        var dataBox = device.ImmediateContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            ffmpeg.av_frame_make_writable(_nv12Frame);

            // 3. Aplica o ponteiro do mouse na imagem BGRA caso esteja visível
            if (_cursorVisible && _cursorShapeBuffer != null && _cursorWidth > 0 && _cursorHeight > 0)
            {

                OverlayCursor(
                    (byte*)dataBox.DataPointer,
                    dataBox.RowPitch,
                    width,
                    height,
                    _cursorShapeBuffer,
                    _cursorWidth,
                    _cursorHeight,
                    _cursorPitch,
                    _cursorX,
                    _cursorY);
            }

            _swsContext = ffmpeg.sws_getCachedContext(
                _swsContext,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_NV12,
                0,
                null,
                null,
                null);

            if (_swsContext == null)
            {
                throw new InvalidOperationException("Não foi possível criar o conversor BGRA para NV12.");
            }

            byte*[] sourceData = { (byte*)dataBox.DataPointer, null, null, null };
            int[] sourceLinesize = { checked((int)dataBox.RowPitch), 0, 0, 0 };
            ffmpeg.sws_scale(
                _swsContext,
                sourceData,
                sourceLinesize,
                0,
                height,
                _nv12Frame->data,
                _nv12Frame->linesize);

            int response = ffmpeg.avcodec_send_frame(_codecContext, _nv12Frame);
            if (response >= 0)
            {
                response = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
                if (response >= 0)
                {
                    byte[] h264Data = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, h264Data, 0, _packet->size);
                    ffmpeg.av_packet_unref(_packet);
                    return h264Data;
                }
            }
        }
        finally
        {
            device.ImmediateContext.Unmap(stagingTexture, 0);
        }

        return null;
    }

    public unsafe void OverlayCursor(
        byte* pFrame,
        uint frameRowPitch,
        int frameWidth,
        int frameHeight,
        byte[] cursorBuffer,
        int cursorWidth,
        int cursorHeight,
        int cursorPitch,
        int cursorX,
        int cursorY)
    {
        // Desenho vetorial da seta padrão do Windows em modo Escuro (Preto com Borda Branca)
        // Matriz 12x19 representando a silhueta clássica do ponteiro
        // 0: Transparente, 1: Borda Branca (RGB 255,255,255), 2: Preenchimento Preto (RGB 0,0,0)
        byte[,] windowsDarkCursorMap = new byte[19, 12]
        {
            { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            { 1, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            { 1, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
            { 1, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0 },
            { 1, 2, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0 },
            { 1, 2, 2, 2, 2, 2, 1, 0, 0, 0, 0, 0 },
            { 1, 2, 2, 2, 2, 2, 2, 1, 0, 0, 0, 0 },
            { 1, 2, 2, 2, 2, 2, 2, 2, 1, 0, 0, 0 },
            { 1, 2, 2, 2, 2, 2, 2, 2, 2, 1, 0, 0 },
            { 1, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0 },
            { 1, 2, 2, 1, 2, 2, 1, 0, 0, 0, 0, 0 },
            { 1, 2, 1, 0, 1, 2, 2, 1, 0, 0, 0, 0 },
            { 1, 1, 0, 0, 1, 2, 2, 1, 0, 0, 0, 0 },
            { 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0, 0 },
            { 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0, 0 },
            { 0, 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0 },
            { 0, 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0 },
            { 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0 }
        };

        int mapHeight = windowsDarkCursorMap.GetLength(0);
        int mapWidth = windowsDarkCursorMap.GetLength(1);

        for (int y = 0; y < mapHeight; y++)
        {
            int targetY = cursorY + y;
            if (targetY < 0 || targetY >= frameHeight) continue;

            byte* pFrameRow = pFrame + (targetY * frameRowPitch);

            for (int x = 0; x < mapWidth; x++)
            {
                int targetX = cursorX + x;
                if (targetX < 0 || targetX >= frameWidth) continue;

                byte pixelType = windowsDarkCursorMap[y, x];
                if (pixelType == 0) continue; // Pixel transparente

                byte* pPixel = pFrameRow + (targetX * 4);

                if (pixelType == 1) // Borda Branca
                {
                    pPixel[0] = 255; // Blue
                    pPixel[1] = 255; // Green
                    pPixel[2] = 255; // Red
                    pPixel[3] = 255; // Alpha
                }
                else if (pixelType == 2) // Preenchimento Preto
                {
                    pPixel[0] = 0;   // Blue
                    pPixel[1] = 0;   // Green
                    pPixel[2] = 0;   // Red
                    pPixel[3] = 255; // Alpha
                }
            }
        }
    }

}