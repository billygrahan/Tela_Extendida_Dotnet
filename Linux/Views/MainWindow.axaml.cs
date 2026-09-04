using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Linux.Network;

namespace Linux.Views;

public partial class MainWindow : Window
{
    private WriteableBitmap? _bitmap;
    private readonly FrameReceiver _receiver = new();

    private int _isRendering = 0; // 0 = false, 1 = true

    public MainWindow()
    {
        InitializeComponent();
        
        // Inicializa a escuta de rede assim que a janela for carregada
        Opened += OnWindowOpened;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
{
    Console.WriteLine("[Cliente] Aguardando broadcast UDP do servidor Windows...");
    
    var listener = new DiscoveryListener();
    IPAddress? serverIp = await listener.ListenForServerAsync();

    if (serverIp != null)
    {
        Console.WriteLine($"[Cliente] Servidor Windows encontrado no IP: {serverIp}");
        
        InitBitmap(1920, 1080);

        _ = Task.Run(() => StartReceiving(serverIp));
    }
    else
    {
        Console.WriteLine("[Cliente] ERRO: Timeout. Nenhum servidor Windows foi encontrado via UDP.");
    }
}

    private void InitBitmap(int width, int height)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // BGRA8888 bate com o formato nativo gerado pelo DXGI no Windows
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            ScreenImage.Source = _bitmap;
        });
    }

    private async Task StartReceiving(IPAddress serverIp)
    {
        // Evento que dispara sempre que um frame/dirty rect for descompactado
        _receiver.OnFrameUnpacked += UpdateScreenBuffer;
        
        await _receiver.ConnectAndReceiveAsync(serverIp);
    }

    

    

    private void UpdateScreenBuffer(int x, int y, int width, int height, byte[] pixelData)
    {
        if (_bitmap == null) return;

        // Se a UI ainda estiver ocupada renderizando o frame anterior, descarta o acúmulo
        if (System.Threading.Interlocked.CompareExchange(ref _isRendering, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
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
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isRendering, 0);
            }
        }, DispatcherPriority.Render);
    }
}