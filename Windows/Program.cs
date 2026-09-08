using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Windows.Network;
using static System.Net.Mime.MediaTypeNames;
using System.Drawing;
using System.Windows.Forms;
using WinForms = System.Windows.Forms;

namespace Windows;



class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // Importações para reanexar ou alocar o terminal e permitir o Console.WriteLine
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    static void Main(string[] args)
    {
        // Garante que as mensagens de Console.WriteLine apareçam no terminal atual
        AttachConsole(ATTACH_PARENT_PROCESS);

        // 1. Definição e validação dos caminhos do FFmpeg
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string ffmpegFolder = Path.Combine(baseDir, "ffmpeg");
        string parentFfmpegFolder = Path.Combine(baseDir, "..", "ffmpeg");

        if (ValidateFfmpegFiles(ffmpegFolder))
        {
            ffmpeg.RootPath = ffmpegFolder;
            SetDllDirectory(ffmpegFolder);
            DynamicallyLoadedBindings.Initialize();
            Console.WriteLine($"\n[FFmpeg] DLLs carregadas do diretório local: {ffmpegFolder}");
        }
        else if (ValidateFfmpegFiles(parentFfmpegFolder))
        {
            string resolvedPath = Path.GetFullPath(parentFfmpegFolder);
            ffmpeg.RootPath = resolvedPath;
            SetDllDirectory(resolvedPath);
            DynamicallyLoadedBindings.Initialize();
            Console.WriteLine($"\n[FFmpeg] DLLs carregadas do diretório pai: {resolvedPath}");
        }
        else
        {
            Console.WriteLine("\n[Erro] DLLs do FFmpeg 6.1 ausentes.");
            return;
        }

        // 2. Inicialização dos componentes visuais
        WinForms.Application.EnableVisualStyles();
        WinForms.Application.SetCompatibleTextRenderingDefault(false);

        // 3. Inicialização dos serviços de rede
        var broadcaster = new DiscoveryBroadcaster();
        broadcaster.Start();

        var streamer = new FrameStreamer();
        streamer.Start();

        // 4. Configuração do ícone da bandeja (System Tray)
        var trayIcon = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ScreenExtender Server (Ativo)",
            Visible = true
        };

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("Encerrar Servidor", null, (s, e) =>
        {
            trayIcon.Visible = false;

            try
            {
                broadcaster.Stop();
                streamer.Stop();
            }
            catch { }

            WinForms.Application.Exit();
            Environment.Exit(0);
        });

        trayIcon.ContextMenuStrip = contextMenu;

        // 5. Execução do loop de mensagens
        WinForms.Application.Run();
    }

    private static bool ValidateFfmpegFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;

        string[] requiredFiles = new[] { "avcodec-60.dll", "avutil-58.dll", "swscale-7.dll" };
        foreach (var file in requiredFiles)
        {
            if (!File.Exists(Path.Combine(folderPath, file))) return false;
        }

        return true;
    }
}

    // versão pegando do sistema, mas não é confiável, pois pode pegar DLLs de outra versão do FFmpeg
    //private static void ValidateFfmpegFiles(string root)
    //{
    //    string[] requiredFiles = { "avcodec-60.dll", "avutil-58.dll", "swscale-7.dll" };
    //    string[] missingFiles = requiredFiles
    //        .Where(file => !File.Exists(Path.Combine(root, file)))
    //        .ToArray();

    //    if (missingFiles.Length > 0)
    //    {
    //        throw new FileNotFoundException(
    //            $"As DLLs do FFmpeg 6.1 não foram encontradas em '{root}'. " +
    //            $"Ausentes: {string.Join(", ", missingFiles)}. " +
    //            "Não use DLLs avcodec-61/62 com FFmpeg.AutoGen 6.1.0.1.",
    //            root);
    //    }

    //    foreach (string file in requiredFiles)
    //    {
    //        Console.WriteLine($"[FFmpeg] DLL encontrada: {file}");
    //    }
    //}
