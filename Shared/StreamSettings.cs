namespace Shared;

public static class StreamSettings
{
    // Rede
    public const int DiscoveryPort = 45678;
    public const int StreamPort = 45679;
    public const string DiscoveryMagicHeader = "SCRN_EXT_DISC_V1";

    // Captura e Codificação
    public const int TargetOutputIndex = 1;
    public const int TargetFps = 60;
    public const int BitRate = 14_000_000;         // Subido levemente para melhorar qualidade a 60 FPS
    public const int KeyFrameInterval = 60;        // 1 Keyframe por segundo a 60 FPS
    public const int MaxBFrames = 0;
    public const string EncoderPixelFormat = "NV12";
    public const string EncoderPreset = "ultrafast";
    public const string EncoderTune = "zerolatency";
    public const string EncoderProfile = "baseline";

    // Desempenho e Transporte
    public const int AcquireNextFrameTimeoutMs = 16; // Sincronizado com o tempo de frame de 60Hz (~16.6ms)
    public const int PacketChannelCapacity = 3;     // Previne drops acidentais durante pequenas variações de tempo
    public const int SocketBufferSize = 2 * 1024 * 1024; // 2MB de buffer TCP para evitar acúmulo na placa de rede
    public const int DecoderThreadCount = 4;
}