using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Shared;

namespace Windows.Capture;

public class H264Encoder : IDisposable
{
    private unsafe AVCodecContext* _codecContext;
    private unsafe AVFrame* _nv12Frame;
    private unsafe AVPacket* _packet;
    private unsafe SwsContext* _swsContext;
    private bool _disposed;

    public unsafe H264Encoder(int width, int height)
    {
        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new InvalidOperationException("Nenhum encoder H.264 disponível no FFmpeg.");

        string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "desconhecido";
        Console.WriteLine($"[FFmpeg] Encoder H.264 selecionado: {codecName}");

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
            throw new InvalidOperationException("Não foi possível alocar o contexto do encoder H.264.");

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
            throw new InvalidOperationException($"Não foi possível abrir o encoder H.264 {codecName}: {openResult}.");

        _nv12Frame = ffmpeg.av_frame_alloc();
        if (_nv12Frame == null)
            throw new InvalidOperationException("Não foi possível alocar o frame NV12.");

        _nv12Frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _nv12Frame->width = width;
        _nv12Frame->height = height;
        ffmpeg.av_frame_get_buffer(_nv12Frame, 32).CheckFFmpegError();

        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
            throw new InvalidOperationException("Não foi possível alocar o pacote H.264.");
    }

    public unsafe byte[]? EncodeFrame(IntPtr dataPointer, uint rowPitch, int width, int height)
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
            throw new InvalidOperationException("Não foi possível criar o conversor BGRA para NV12.");

        byte*[] sourceData = { (byte*)dataPointer, null, null, null };
        int[] sourceLinesize = { checked((int)rowPitch), 0, 0, 0 };

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

        return null;
    }

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_swsContext != null) ffmpeg.sws_freeContext(_swsContext);
            if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); }
            if (_nv12Frame != null) { fixed (AVFrame** f = &_nv12Frame) ffmpeg.av_frame_free(f); }
            if (_codecContext != null) { fixed (AVCodecContext** c = &_codecContext) ffmpeg.avcodec_free_context(c); }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}