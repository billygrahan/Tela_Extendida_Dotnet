using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
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
        _codecContext->time_base = new AVRational { num = 1, den = 60 };
        _codecContext->framerate = new AVRational { num = 60, den = 1 };
        _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        _codecContext->bit_rate = 12_000_000;
        _codecContext->gop_size = 30;
        _codecContext->max_b_frames = 0;

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "preset", "ultrafast", 0);
        ffmpeg.av_dict_set(&options, "tune", "zerolatency", 0);
        ffmpeg.av_dict_set(&options, "profile", "baseline", 0);

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

    // Removido 'unsafe' daqui para permitir 'await' sem erros
    public async Task StartCaptureAndStreamAsync(Stream networkStream)
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
        for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
        {
            targetOutput = output;
            break;
        }

        if (targetOutput == null)
        {
            throw new InvalidOperationException("Nenhum monitor DXGI foi encontrado para captura.");
        }

        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        using var outputDuplication = output1.DuplicateOutput(device);

        bool encoderInitialized = false;

        var packetChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        // Task de envio assíncrono via rede
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
        byte[]? rawPixelBuffer = null;

        while (true)
        {
            var result = outputDuplication.AcquireNextFrame(16, out _, out var desktopResource);

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
                        rawPixelBuffer = new byte[width * height * 4];
                    }

                    device.ImmediateContext.CopyResource(stagingTexture!, texture2D);

                    byte[]? h264Packet = EncodeFrameToH264(device, stagingTexture!, rawPixelBuffer!, width, height);
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

    private unsafe byte[]? EncodeFrameToH264(ID3D11Device device, ID3D11Texture2D stagingTexture, byte[] buffer, int width, int height)
    {
        var dataBox = device.ImmediateContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            ffmpeg.av_frame_make_writable(_nv12Frame);

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
}