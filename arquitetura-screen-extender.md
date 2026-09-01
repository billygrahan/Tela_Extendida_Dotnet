# Arquitetura do projeto: extensão de tela Windows → Ubuntu via rede

## Visão geral

Dois processos .NET independentes, cada um numa máquina, conectados por TCP sobre um link de rede dedicado (cabo Ethernet direto):

- **Servidor (Windows)**: expõe um monitor virtual, captura seus frames, comprime e envia.
- **Cliente (Ubuntu)**: recebe, decodifica e renderiza em tela cheia.

Comunicação em uma via só (Windows → Ubuntu) para o MVP. Input redirection (mouse/teclado do notebook controlando o Windows) fica pra uma fase posterior, opcional.

---

## 1. Estrutura de solução recomendada

```
ScreenExtender/
├── ScreenExtender.sln
├── ScreenExtender.Server/          (Windows, .NET 8, console ou WinForms tray app)
│   ├── Capture/
│   │   ├── VirtualDisplayManager.cs
│   │   └── DesktopDuplicator.cs
│   ├── Compression/
│   │   └── DirtyRectCompressor.cs
│   ├── Network/
│   │   ├── DiscoveryBroadcaster.cs
│   │   └── FrameStreamer.cs
│   └── Program.cs
├── ScreenExtender.Client/          (Ubuntu, .NET 8, Avalonia)
│   ├── Network/
│   │   ├── DiscoveryListener.cs
│   │   └── FrameReceiver.cs
│   ├── Rendering/
│   │   └── FrameCanvas.cs
│   ├── App.axaml
│   ├── MainWindow.axaml(.cs)
│   └── Program.cs
└── ScreenExtender.Shared/          (netstandard2.1, referenciado pelos dois)
    ├── Protocol/
    │   ├── FrameHeader.cs
    │   └── DiscoveryMessage.cs
    └── Constants.cs
```

O projeto `Shared` garante que o protocolo de rede (formato do cabeçalho do frame, mensagens de descoberta) seja definido uma única vez e usado dos dois lados — evita bugs de serialização por dessincronia.

---

## 2. Lado Windows (Servidor)

### 2.1 Monitor virtual — driver IddCx

Extensão de tela de verdade exige que o Windows enxergue um monitor a mais. Escrever um driver kernel-mode do zero é um projeto à parte (C++, WDK, assinatura de driver). Recomendo usar o projeto open-source **[Virtual-Display-Driver](https://github.com/itsmikethetech/Virtual-Display-Driver)** (MIT):

- Instala como um driver comum (modo teste do Windows habilitado, ou driver assinado).
- Permite configurar resolução e taxa de atualização via um arquivo de config.
- Depois de instalado, o Windows trata como um monitor real — aparece em Configurações de Vídeo, aceita ser um monitor "estendido".

Seu programa C# não interage com o driver diretamente — ele só precisa **encontrar esse monitor virtual** entre os monitores do sistema (via `Screen.AllScreens` do WinForms ou enumeração DXGI) pra saber qual capturar.

### 2.2 Captura de tela — Desktop Duplication API

**Pacote**: `Vortice.Windows` (bindings modernos de DXGI/Direct3D11 para .NET)

```
dotnet add package Vortice.DXGI
dotnet add package Vortice.Direct3D11
```

Fluxo:
1. Enumerar adaptadores DXGI (`IDXGIFactory1`) até achar o output correspondente ao monitor virtual.
2. Criar um `IDXGIOutputDuplication` nesse output.
3. Chamar `AcquireNextFrame` em loop — a API já devolve **apenas as regiões alteradas** (`DirtyRects`) desde o último frame, o que é ouro pra performance: você não precisa comprimir a tela inteira a cada frame, só o que mudou.
4. Copiar a textura da GPU para memória do sistema (staging texture) pra poder ler os bytes em C#.

Essa é a parte mais sensível a bugs — vale começar com um teste isolado que só imprime "X dirty rects detectados" antes de plugar compressão/rede.

### 2.3 Compressão

Como você priorizou **qualidade de imagem** e o link é cabeado (banda alta, baixa latência), a recomendação é compressão **lossless** por região alterada, não vídeo com perdas:

**Pacote**: `ZstdSharp.Port` (bindings de Zstandard, mais rápido e com melhor taxa que LZ4/Deflate pra esse tipo de dado)

Para cada dirty rect: extrai os pixels daquela região → comprime com Zstd nível médio (nível 3-6, equilíbrio velocidade/taxa) → serializa junto com as coordenadas do rect.

> Nota: se no futuro quiser suportar conteúdo com muito movimento (vídeo, jogos) sem estourar a banda, dá pra adicionar um segundo modo usando H.264 via `Vortice.MediaFoundation` — mas comece sem isso, é complexidade que você não precisa ainda.

### 2.4 Transporte de rede

`System.Net.Sockets` puro (TCP), sem frameworks adicionais — mais previsível para streaming binário contínuo:

- **Descoberta**: broadcast UDP na porta 45678, como já vimos — servidor anuncia presença, cliente escuta e extrai o IP de origem do pacote.
- **Streaming**: TCP na porta 45679. Protocolo simples por frame:

```
[4 bytes: tamanho total do payload]
[2 bytes: quantidade de dirty rects nesse frame]
Para cada rect:
  [4 bytes x, 4 bytes y, 4 bytes width, 4 bytes height]
  [4 bytes: tamanho dos dados comprimidos]
  [N bytes: dados Zstd-comprimidos]
```

Um `NetworkStream` com `BinaryWriter`/`BinaryReader` já dá conta disso sem precisar de bibliotecas de serialização.

---

## 3. Lado Ubuntu (Cliente)

### 3.1 Framework de UI

.NET no Linux não tem WPF/WinForms. Use **Avalonia UI** — framework de UI multiplataforma em C# com API bem próxima de WPF (XAML, bindings, etc.), com ótimo suporte a Linux/X11 e Wayland.

```
dotnet new avalonia.app -o ScreenExtender.Client
```

### 3.2 Janela fullscreen borderless

No `MainWindow.axaml.cs`:
```csharp
WindowState = WindowState.FullScreen;
SystemDecorations = SystemDecorations.None;
```

### 3.3 Recepção e decodificação

- Conecta no IP descoberto via UDP broadcast (porta 45679, TCP).
- Loop de leitura: lê o cabeçalho, lê os N rects comprimidos, descomprime cada um com `ZstdSharp.Port` (mesmo pacote dos dois lados, já que é multiplataforma).

### 3.4 Renderização

A forma mais direta com boa performance no Avalonia é manter um `WriteableBitmap` do tamanho da tela, e a cada frame recebido:
1. Para cada dirty rect descomprimido, escrever os pixels diretamente no buffer do bitmap na região correspondente (usando `ILockedFramebuffer`).
2. Invalidar só a área alterada (`InvalidateVisual` com o rect, quando possível) pra não redesenhar a tela inteira à toa.

Isso evita decodificar/desenhar regiões que não mudaram — mantendo a CPU/GPU do notebook livre mesmo em telas grandes.

---

## 4. Roadmap de implementação (ordem sugerida)

| Fase | O que fazer | Critério de "pronto" |
|---|---|---|
| **0. Rede** | Configurar cabo Ethernet direto, IP automático (APIPA/link-local) nos dois lados | `ping` funciona nos dois sentidos |
| **1. Descoberta** | Implementar broadcast UDP + listener | Cliente imprime no console o IP do servidor automaticamente |
| **2. Captura crua** | Desktop Duplication API capturando o monitor **físico principal** (não o virtual ainda) só pra validar a captura, salvando frames como PNG em disco | Consegue ver os PNGs capturados corretamente |
| **3. Streaming sem compressão** | Servidor manda a tela inteira sem compressão via TCP; cliente recebe e desenha num `WriteableBitmap` simples, sem otimização | Você vê a tela do Windows aparecendo no Ubuntu, mesmo que lento |
| **4. Dirty rects + compressão** | Trocar "tela inteira" por dirty rects comprimidos com Zstd | Uso de banda cai bastante parado no desktop; FPS sobe |
| **5. Monitor virtual** | Instalar o Virtual-Display-Driver, apontar a captura pra esse monitor em vez do físico | Consegue arrastar uma janela do Windows pro monitor virtual e vê-la aparecer no Ubuntu |
| **6. Polish** | Reconexão automática se a rede cair, indicador de status, configuração de resolução | Uso "de produção" no dia a dia |
| **7. (Opcional) Input redirection** | Capturar mouse/teclado no Ubuntu quando o cursor "entra" na tela virtual e reenviar pro Windows via `SendInput` | Consegue usar o trackpad do notebook controlando aquele monitor |

Cada fase é testável isoladamente antes de emendar na próxima — evita depurar captura + rede + renderização todos juntos quando algo dá errado.

---

## 5. Resumo de tecnologias

| Camada | Tecnologia |
|---|---|
| Linguagem/runtime | C# / .NET 8 (multiplataforma) |
| Monitor virtual (Windows) | Virtual-Display-Driver (open-source, IddCx) |
| Captura de tela | Vortice.Windows (DXGI Desktop Duplication API) |
| Compressão | ZstdSharp.Port |
| UI do cliente | Avalonia UI |
| Rede | System.Net.Sockets (TCP + UDP broadcast) puro |
| Protocolo | Binário customizado, definido no projeto Shared |
