using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Shared;
using Vortice.Direct3D11;
using Vortice.DXGI;

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

public class DxgiCapturer : IDisposable
{
    private ID3D11Device? _device;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private H264Encoder? _encoder;

    private int _cursorX;
    private int _cursorY;
    private bool _cursorVisible;
    private bool _disposed;

    public async Task StartCaptureAndStreamAsync(Stream networkStream, CancellationToken cancellationToken = default)
    {
        // Cria um CTS vinculado ao CancellationToken externo para cancelar a captura em caso de erro de rede
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.None,
            null,
            out _device).CheckError();

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        IDXGIOutput? targetOutput = null;
        for (uint outputIndex = 0; ; outputIndex++)
        {
            var outputResult = adapter.EnumOutputs(outputIndex, out var output);
            if (!outputResult.Success) break;

            if (outputIndex == StreamSettings.TargetOutputIndex)
                targetOutput = output;
            else
                output.Dispose();
        }

        if (targetOutput == null)
            throw new InvalidOperationException($"O output DXGI de índice {StreamSettings.TargetOutputIndex} não foi encontrado.");

        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);

        var packetChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(StreamSettings.PacketChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        // Task consumidora gravando no Socket de rede
        var writerTask = Task.Run(async () =>
        {
            var reader = packetChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(token))
                {
                    while (reader.TryRead(out var packet))
                    {
                        byte[] sizeHeader = BitConverter.GetBytes(packet.Length);
                        await networkStream.WriteAsync(sizeHeader, 0, 4, token);
                        await networkStream.WriteAsync(packet, 0, packet.Length, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[DxgiCapturer] Cliente desconectado (Rede): {ex.Message}");
                linkedCts.Cancel(); // Cancela o loop de captura DXGI imediatamente!
            }
        }, token);

        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                var result = _duplication.AcquireNextFrame(StreamSettings.AcquireNextFrameTimeoutMs, out var frameInfo, out var desktopResource);

                if (result.Success)
                {
                    using (desktopResource)
                    {
                        using var texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();
                        int width = (int)texture2D.Description.Width;
                        int height = (int)texture2D.Description.Height;

                        if (_encoder == null)
                        {
                            _encoder = new H264Encoder(width, height);

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

                            _stagingTexture = _device.CreateTexture2D(textureDesc);
                        }

                        if (frameInfo.PointerPosition.Visible)
                        {
                            _cursorVisible = true;
                            _cursorX = frameInfo.PointerPosition.Position.X;
                            _cursorY = frameInfo.PointerPosition.Position.Y;
                        }

                        _device.ImmediateContext.CopyResource(_stagingTexture!, texture2D);

                        var dataBox = _device.ImmediateContext.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            if (_cursorVisible)
                            {
                                unsafe
                                {
                                    CursorOverlay.Apply(
                                        (byte*)dataBox.DataPointer,
                                        dataBox.RowPitch,
                                        width,
                                        height,
                                        _cursorX,
                                        _cursorY);
                                }
                            }

                            byte[]? h264Packet = _encoder.EncodeFrame(dataBox.DataPointer, dataBox.RowPitch, width, height);
                            if (h264Packet != null)
                            {
                                packetChannel.Writer.TryWrite(h264Packet);
                            }
                        }
                        finally
                        {
                            _device.ImmediateContext.Unmap(_stagingTexture!, 0);
                        }
                    }

                    _duplication.ReleaseFrame();
                }
                else if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    await Task.Delay(1, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[DxgiCapturer] Encerrando captura DXGI: {ex.Message}");
        }
        finally
        {
            packetChannel.Writer.TryComplete();
            try { await writerTask; } catch { }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    _encoder?.Dispose();
                    _encoder = null;

                    _stagingTexture?.Dispose();
                    _stagingTexture = null;

                    if (_duplication != null)
                    {
                        try { _duplication.ReleaseFrame(); } catch { }
                        _duplication.Dispose();
                        _duplication = null;
                    }

                    _device?.Dispose();
                    _device = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DxgiCapturer] Erro ao liberar recursos Direct3D/DXGI: {ex.Message}");
                }
            }
            _disposed = true;
        }
    }

    ~DxgiCapturer()
    {
        Dispose(false);
    }
}