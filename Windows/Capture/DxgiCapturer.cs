using System;
using System.IO;
using System.Runtime.InteropServices;
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

public enum PacketType : byte
{
    Video = 0,
    Cursor = 1
}

public class NetworkPacket
{
    public PacketType Type { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public class DxgiCapturer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    private ID3D11Device? _device;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private H264Encoder? _encoder;

    private int _cursorX;
    private int _cursorY;
    private bool _cursorVisible;
    private bool _disposed;

    private int _monitorLeft;
    private int _monitorTop;

    public async Task StartCaptureAndStreamAsync(Stream networkStream, CancellationToken cancellationToken = default)
    {
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
        {
            Console.WriteLine($"[DxgiCapturer] Output no índice {StreamSettings.TargetOutputIndex} não encontrado. Usando monitor 0.");
            adapter.EnumOutputs(0, out targetOutput);
        }

        if (targetOutput == null)
            throw new InvalidOperationException("Nenhum display DXGI ativo foi encontrado.");

        // Guarda o deslocamento do monitor na área de trabalho do Windows
        var desktopBounds = targetOutput.Description.DesktopCoordinates;
        _monitorLeft = desktopBounds.Left;
        _monitorTop = desktopBounds.Top;

        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);

        var packetChannel = Channel.CreateBounded<NetworkPacket>(new BoundedChannelOptions(StreamSettings.PacketChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        // Task 1: Envio de pacotes via TCP
        var writerTask = Task.Run(async () =>
        {
            var reader = packetChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(token))
                {
                    while (reader.TryRead(out var packet))
                    {
                        await networkStream.WriteAsync(new byte[] { (byte)packet.Type }, 0, 1, token);
                        byte[] sizeHeader = BitConverter.GetBytes(packet.Data.Length);
                        await networkStream.WriteAsync(sizeHeader, 0, 4, token);
                        await networkStream.WriteAsync(packet.Data, 0, packet.Data.Length, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[DxgiCapturer] Erro na rede: {ex.Message}");
                linkedCts.Cancel();
            }
        }, token);

        // Task 2: Captura assíncrona do Cursor a 60 FPS independentes
        var cursorTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                CheckAndSendCursor(packetChannel);
                await Task.Delay(16, token);
            }
        }, token);

        // Task 3 (Principal): Captura de Vídeo DXGI
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

                        _device.ImmediateContext.CopyResource(_stagingTexture!, texture2D);

                        var dataBox = _device.ImmediateContext.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            byte[]? h264Packet = _encoder.EncodeFrame(dataBox.DataPointer, dataBox.RowPitch, width, height);
                            if (h264Packet != null)
                            {
                                packetChannel.Writer.TryWrite(new NetworkPacket { Type = PacketType.Video, Data = h264Packet });
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
            Console.WriteLine($"[DxgiCapturer] Encerrando captura: {ex.Message}");
        }
        finally
        {
            packetChannel.Writer.TryComplete();
            try { await Task.WhenAll(writerTask, cursorTask); } catch { }
        }
    }

    private void CheckAndSendCursor(Channel<NetworkPacket> channel)
    {
        var cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (GetCursorInfo(out cursorInfo))
        {
            bool isVisible = (cursorInfo.flags & CURSOR_SHOWING) != 0;

            // Subtrai a posição inicial do monitor capturado
            int localX = cursorInfo.ptScreenPos.x - _monitorLeft;
            int localY = cursorInfo.ptScreenPos.y - _monitorTop;

            if (isVisible)
            {
                if (!_cursorVisible || localX != _cursorX || localY != _cursorY)
                {
                    _cursorVisible = true;
                    _cursorX = localX;
                    _cursorY = localY;

                    string cursorJson = $"{{\"X\":{_cursorX}, \"Y\":{_cursorY}, \"Visible\":true}}";
                    byte[] cursorData = System.Text.Encoding.UTF8.GetBytes(cursorJson);
                    channel.Writer.TryWrite(new NetworkPacket { Type = PacketType.Cursor, Data = cursorData });
                }
            }
            else if (_cursorVisible)
            {
                _cursorVisible = false;
                string cursorJson = "{\"Visible\":false}";
                byte[] cursorData = System.Text.Encoding.UTF8.GetBytes(cursorJson);
                channel.Writer.TryWrite(new NetworkPacket { Type = PacketType.Cursor, Data = cursorData });
            }
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
                    Console.WriteLine($"[DxgiCapturer] Erro no Dispose: {ex.Message}");
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