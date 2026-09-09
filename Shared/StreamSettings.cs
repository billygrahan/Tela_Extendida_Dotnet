namespace Shared;

public static class StreamSettings
{
    // Rede
    public const int DiscoveryPort = 45678;
    public const int StreamPort = 45679;
    public const string DiscoveryMagicHeader = "SCRN_EXT_DISC_V1";

    // Captura e codificacao
    public const int TargetOutputIndex = 1;
    public const int TargetFps = 60;
    public const int BitRate = 12_000_000;
    public const int KeyFrameInterval = 30;
    public const int MaxBFrames = 0;
    public const string EncoderPixelFormat = "NV12";
    public const string EncoderPreset = "ultrafast";
    public const string EncoderTune = "zerolatency";
    public const string EncoderProfile = "baseline";

    // Desempenho e transporte
    public const int AcquireNextFrameTimeoutMs = 8;
    public const int PacketChannelCapacity = 1;
    public const int SocketBufferSize = 1024 * 1024;
    public const int DecoderThreadCount = 4;
}
