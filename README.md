# WinToLin

Extensor de tela Windows para Linux/Ubuntu pela rede local.

O servidor Windows captura o primeiro monitor DXGI, codifica os frames em H.264 e envia os pacotes por TCP. O cliente Linux decodifica H.264 com FFmpeg e renderiza os frames em uma janela Avalonia.

## Estado atual

- Windows: captura via Desktop Duplication/DXGI.
- Codec: H.264 através do encoder disponível no FFmpeg.
- Cliente: Avalonia + FFmpeg.AutoGen.
- Transporte: TCP na porta `45679`.
- Descoberta: broadcast UDP na porta `45678`.
- Versão obrigatória do binding: `FFmpeg.AutoGen 6.1.0.1`.
- Aceleração AMF não é obrigatória e não é garantida: `h264_amf` depende de GPU AMD e driver compatível. O código procura o encoder H.264 disponível no FFmpeg.

## Requisitos

Em ambos os computadores:

- .NET SDK 10.
- Rede local entre Windows e Linux.
- Firewall permitindo UDP `45678` e TCP `45679` no Windows.
- Bibliotecas nativas FFmpeg 6.1 compatíveis com `FFmpeg.AutoGen 6.1.0.1`.

As bibliotecas nativas não são instaladas pelo pacote NuGet. `FFmpeg.AutoGen` fornece apenas os bindings .NET.

## FFmpeg no Windows

Use uma build **shared** do FFmpeg 6.1. A pasta `bin` precisa conter, no mínimo:

```text
avcodec-60.dll
avutil-58.dll
swscale-7.dll
```

Copie também todas as outras DLLs da mesma pasta, pois elas podem ser dependências indiretas.

Exemplo de instalação:

```text
C:\ffmpeg-6.1\bin\avcodec-60.dll
C:\ffmpeg-6.1\bin\avutil-58.dll
C:\ffmpeg-6.1\bin\swscale-7.dll
```

O WinGet normalmente instala uma versão recente, como FFmpeg 9. Essa versão não é compatível com este projeto enquanto o binding permanecer em `6.1.0.1`. Não use `avcodec-61.dll` ou `avcodec-62.dll` com este código.

No PowerShell, aponte o processo para a instalação:

```powershell
$env:FFMPEG_ROOT = "C:\ffmpeg-6.1\bin"

Get-ChildItem "$env:FFMPEG_ROOT\avcodec-60.dll",
			  "$env:FFMPEG_ROOT\avutil-58.dll",
			  "$env:FFMPEG_ROOT\swscale-7.dll"
```

O processo precisa encontrar os três arquivos. A variável vale apenas para o PowerShell atual. Para torná-la permanente:

```powershell
[Environment]::SetEnvironmentVariable("FFMPEG_ROOT", "C:\ffmpeg-6.1\bin", "User")
```

## FFmpeg no Linux/Ubuntu

Instale as bibliotecas do sistema:

```bash
sudo apt update
sudo apt install ffmpeg libavcodec-dev libavutil-dev libswscale-dev
```

Verifique a ABI instalada:

```bash
ffmpeg -version
ldconfig -p | grep -E 'libav(codec|util|swscale)'
```

O projeto espera:

```text
libavcodec.so.60
libavutil.so.58
libswscale.so.7
```

Em Ubuntu x64, o cliente encontra automaticamente `/usr/lib/x86_64-linux-gnu`. Para informar outro local:

```bash
export FFMPEG_ROOT=/caminho/para/as/bibliotecas
```

## Dependências nativas distribuídas com o projeto

Também é possível não depender de uma instalação global. Coloque as bibliotecas no workspace:

```text
Windows/runtimes/win-x64/native/*.dll
Linux/runtimes/linux-x64/native/*.so*
```

Os arquivos são copiados automaticamente para `bin` e `publish`. O programa usa essa pasta quando ela contém as bibliotecas corretas; caso contrário, procura as bibliotecas do sistema no Linux.

## Compilar

Na raiz do projeto:

```bash
dotnet restore
dotnet build Windows/Windows.csproj
dotnet build Linux/Linux.csproj
```

O projeto pode ser compilado no Linux apenas para validar o código Windows, mas o servidor DXGI precisa ser executado no Windows.

## Executar o servidor Windows

No PowerShell:

```powershell
cd C:\Users\Usuario\Documents\billygrahan\Tela_Extendida_Dotnet
$env:FFMPEG_ROOT = "C:\ffmpeg-6.1\bin"
dotnet run --project Windows\Windows.csproj
```

Logs esperados:

```text
[FFmpeg] Procurando DLLs em: C:\ffmpeg-6.1\bin
[FFmpeg] DLL encontrada: avcodec-60.dll
[FFmpeg] DLL encontrada: avutil-58.dll
[FFmpeg] DLL encontrada: swscale-7.dll
[FrameStreamer] Aguardando conexões TCP na porta 45679...
```

## Executar o cliente Linux

```bash
cd ~/Documentos/Tela_Extendida_Dotnet
export FFMPEG_ROOT=/usr/lib/x86_64-linux-gnu
dotnet run --project Linux/Linux.csproj
```

Logs esperados:

```text
[FFmpeg] Procurando bibliotecas em: /usr/lib/x86_64-linux-gnu
[Cliente] Aguardando broadcast UDP do servidor Windows...
[FrameReceiver] Conexão TCP estabelecida com sucesso!
```

## Firewall do Windows

Execute o PowerShell como administrador:

```powershell
New-NetFirewallRule -DisplayName "WinToLin UDP Discovery" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45678
New-NetFirewallRule -DisplayName "WinToLin TCP Stream" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45679
```

## Configuração de captura e streaming

Os valores ajustáveis estão centralizados em [Shared/StreamSettings.cs](Shared/StreamSettings.cs):

| Variável | Valor atual | Função |
|---|---:|---|
| `TargetOutputIndex` | `0` | Índice do monitor DXGI capturado |
| `TargetFps` | `60` | Taxa desejada do encoder |
| `BitRate` | `12000000` | Bitrate H.264 em bits por segundo |
| `KeyFrameInterval` | `30` | Intervalo entre keyframes |
| `MaxBFrames` | `0` | B-frames; zero reduz latência |
| `EncoderPixelFormat` | `NV12` | Formato de entrada do encoder |
| `EncoderPreset` | `ultrafast` | Preset de codificação |
| `EncoderTune` | `zerolatency` | Ajuste para baixa latência |
| `EncoderProfile` | `baseline` | Perfil H.264 compatível |
| `AcquireNextFrameTimeoutMs` | `16` | Timeout da captura DXGI |
| `PacketChannelCapacity` | `2` | Frames aguardando envio |
| `SocketBufferSize` | `1048576` | Buffer TCP em bytes |
| `DecoderThreadCount` | `2` | Threads do decoder Linux |
| `DiscoveryPort` | `45678` | Porta de descoberta UDP |
| `StreamPort` | `45679` | Porta do vídeo TCP |

A resolução não é definida manualmente no momento: ela é obtida do monitor retornado pelo DXGI. Para mudar o monitor capturado, altere `TargetOutputIndex`.

## Diagnóstico rápido

### `NotSupportedException` em `avcodec_find_encoder` ou `avcodec_find_decoder`

As DLLs nativas não foram carregadas ou têm ABI diferente do binding. Confirme que `FFmpeg.AutoGen 6.1.0.1` está nos dois `.csproj` e que as bibliotecas são FFmpeg 6.1.

### DLL ausente

O erro indica exatamente o diretório pesquisado e os arquivos ausentes. Verifique `FFMPEG_ROOT` e não aponte para uma pasta que contenha apenas `ffmpeg.exe`.

### Cliente conecta e desconecta sem receber frames

O TCP está funcionando, mas o servidor falhou ao iniciar DXGI ou o encoder. Consulte o stack trace do servidor Windows. O encoder selecionado é impresso no log como `Encoder H.264 selecionado`.

### Tela preta

Verifique primeiro se o servidor produziu pacotes. Se produziu, valide o decoder Linux. Se não produziu, valide captura DXGI, encoder e conversão BGRA para NV12 no Windows.

## Limitações atuais

- O transporte ainda é TCP; perda de pacote pode aumentar a latência.
- A captura usa o primeiro monitor DXGI por padrão.
- Não há reconexão automática.
- Não há redirecionamento de teclado e mouse.
- A aceleração de hardware depende do encoder exposto pela instalação do FFmpeg e da GPU/driver.
