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
        
        // Inicializa a escuta de rede assim que a janela for carregada
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

    // Tratamento de atalhos de teclado para controle de tela cheia
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