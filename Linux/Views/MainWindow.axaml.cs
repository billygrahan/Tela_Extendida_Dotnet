using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Linux.Network;

namespace Linux.Views;

public partial class MainWindow : Window
{
    private WriteableBitmap? _bitmap;
    private readonly FrameReceiver _receiver = new();

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Console.WriteLine("[Cliente] Aguardando broadcast UDP do servidor Windows...");

        _ = Task.Run(async () =>
        {
            try
            {
                var listener = new DiscoveryListener();
                IPAddress? serverIp = await listener.ListenForServerAsync();

                if (serverIp != null)
                {
                    Console.WriteLine($"[Cliente] Servidor Windows encontrado no IP: {serverIp}");
                    
                    _receiver.OnFrameUnpacked += UpdateScreenBuffer;
                    _receiver.OnCursorMoved += UpdateCursorPosition; // Inscreve no evento correto

                    await _receiver.ConnectAndReceiveAsync(serverIp);
                }
                else
                {
                    Console.WriteLine("[Cliente] ERRO: Timeout. Nenhum servidor Windows foi encontrado via UDP.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cliente] Erro durante a conexão: {ex.Message}");
            }
        });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
        }
    }

    // Recebe o objeto CursorState do FrameReceiver
    private void UpdateCursorPosition(FrameReceiver.CursorState cursor)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CursorPointer.IsVisible = cursor.Visible;

            if (!cursor.Visible || _bitmap == null) return;

            // Resolução original do vídeo recebido (ex: 1920x1080)
            double nativeWidth = _bitmap.PixelSize.Width;
            double nativeHeight = _bitmap.PixelSize.Height;

            // Resolução atual da imagem renderizada na janela no Linux
            double renderWidth = ScreenImage.Bounds.Width;
            double renderHeight = ScreenImage.Bounds.Height;

            if (nativeWidth <= 0 || nativeHeight <= 0 || renderWidth <= 0 || renderHeight <= 0) return;

            // Calcula os fatores de escala
            double scaleX = renderWidth / nativeWidth;
            double scaleY = renderHeight / nativeHeight;

            // Aplica a proporção nas coordenadas X e Y
            double scaledX = cursor.X * scaleX;
            double scaledY = cursor.Y * scaleY;

            Canvas.SetLeft(CursorPointer, scaledX);
            Canvas.SetTop(CursorPointer, scaledY);
        }, DispatcherPriority.Render);
    }

    private void UpdateScreenBuffer(int x, int y, int width, int height, byte[] pixelData)
    {
        if (width <= 0 || height <= 0 || pixelData.Length == 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);

                ScreenImage.Source = _bitmap;
            }

            using (var lockBuffer = _bitmap.Lock())
            {
                unsafe
                {
                    byte* ptr = (byte*)lockBuffer.Address.ToPointer();
                    int stride = lockBuffer.RowBytes;

                    fixed (byte* srcPtr = pixelData)
                    {
                        if (stride == width * 4 && x == 0 && y == 0)
                        {
                            Buffer.MemoryCopy(srcPtr, ptr, pixelData.Length, pixelData.Length);
                        }
                        else
                        {
                            for (int row = 0; row < height; row++)
                            {
                                int destOffset = ((y + row) * stride) + (x * 4);
                                int srcOffset = row * width * 4;

                                Buffer.MemoryCopy(srcPtr + srcOffset, ptr + destOffset, width * 4, width * 4);
                            }
                        }
                    }
                }
            }

            ScreenImage.InvalidateVisual();
        }, DispatcherPriority.Render);
    }
}