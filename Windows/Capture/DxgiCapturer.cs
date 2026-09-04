using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Shared;
using ZstdSharp;

namespace Windows.Capture;

public class DxgiCapturer
{
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
            if (i > 0 || targetOutput == null)
            {
                targetOutput = output;
            }
        }

        if (targetOutput == null) return;

        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        using var outputDuplication = output1.DuplicateOutput(device);

        var frameChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        var rawFrameChannel = Channel.CreateBounded<(byte[] data, int width, int height)>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        _ = Task.Run(async () =>
        {
            using var compressor = new Compressor(3);
            var reader = rawFrameChannel.Reader;

            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var frame))
                {
                    var (rawBuffer, width, height) = frame;
                    byte[] compressedPixels = compressor.Wrap(rawBuffer).ToArray();

                    using var memoryStream = new MemoryStream();
                    using var writer = new BinaryWriter(memoryStream);

                    var frameHeader = new FrameHeader { RectCount = 1 };
                    frameHeader.Serialize(writer);

                    var rectHeader = new RectHeader
                    {
                        X = 0,
                        Y = 0,
                        Width = width,
                        Height = height,
                        CompressedDataSize = compressedPixels.Length
                    };
                    rectHeader.Serialize(writer);
                    writer.Write(compressedPixels);

                    frameChannel.Writer.TryWrite(memoryStream.ToArray());
                }
            }
        });

        _ = Task.Run(async () =>
        {
            var reader = frameChannel.Reader;
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
        var stopwatch = new Stopwatch();
        int frameCounter = 0;
        const int TARGET_FPS = 30;
        const int FRAME_TIME_MS = 1000 / TARGET_FPS;

        while (true)
        {
            stopwatch.Restart();

            var result = outputDuplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);

            if (result.Success)
            {
                using (desktopResource)
                {
                    using var texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();

                    int width = (int)texture2D.Description.Width;
                    int height = (int)texture2D.Description.Height;

                    if (stagingTexture == null)
                    {
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

                    device.ImmediateContext.CopyResource(stagingTexture, texture2D);
                    ExtractPixelsFast(device, stagingTexture, rawPixelBuffer!, width, height);

                    byte[] frameCopy = new byte[rawPixelBuffer.Length];
                    Array.Copy(rawPixelBuffer, frameCopy, rawPixelBuffer.Length);

                    rawFrameChannel.Writer.TryWrite((frameCopy, width, height));

                    frameCounter++;
                }

                outputDuplication.ReleaseFrame();
            }
            else if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                await Task.Delay(1);
                continue;
            }

            long processingTime = stopwatch.ElapsedMilliseconds;
            long delay = FRAME_TIME_MS - processingTime;

            if (delay > 0)
            {
                await Task.Delay((int)delay);
            }
        }
    }

    private unsafe void ExtractPixelsFast(ID3D11Device device, ID3D11Texture2D stagingTexture, byte[] destinationBuffer, int width, int height)
    {
        var dataBox = device.ImmediateContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            byte* srcPtr = (byte*)dataBox.DataPointer.ToPointer();
            int rowPitch = (int)dataBox.RowPitch;
            int bytesPerLine = width * 4;

            fixed (byte* dstPtr = destinationBuffer)
            {
                if (rowPitch == bytesPerLine)
                {
                    Buffer.MemoryCopy(srcPtr, dstPtr, destinationBuffer.Length, destinationBuffer.Length);
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (row * rowPitch),
                            dstPtr + (row * bytesPerLine),
                            bytesPerLine,
                            bytesPerLine);
                    }
                }
            }
        }
        finally
        {
            device.ImmediateContext.Unmap(stagingTexture, 0);
        }
    }
}
