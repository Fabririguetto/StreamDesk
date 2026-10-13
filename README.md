# StreamDesk 🚀
### Solución de Streaming de Pantalla de Ultra Baja Latencia sobre LAN Ethernet (< 50 ms) para Windows

**StreamDesk** es una solución de alto rendimiento desarrollada en **C# (.NET 8)** para transmitir el escritorio de una **Notebook (Emisor)** a una **PC de Escritorio (Receptor)** a través de una red local Ethernet cableada, visualizándose en una ventana dedicada con aceleración de GPU y control KVM inverso.

---

## 🏗️ Arquitectura del Sistema

La solución está dividida en tres proyectos modulares:

1. **`StreamDesk.Common`**:
   - Biblioteca compartida con contratos de red y serialización binaria.
   - **`LanDiscoveryService`**: Descubrimiento Zero-Config en LAN vía UDP Broadcast (puerto `45454`).
   - **`RtpPacket` / `RtpDepacketizer`**: Fragmentación y reensamblado en tiempo real según RFC 6184 (Single NALU y fragmentos FU-A) sobre UDP.
   - **`ControlPacket`**: Estructura binaria de 16 bytes para reenvío de mouse y teclado sin latencia.

2. **`StreamDesk.Sender` (Notebook)**:
   - **Captura GPU Zero-Copy**: Utiliza `Windows.Graphics.Capture` (óptimo para laptops con GPU híbrida Intel/AMD + NVIDIA) con fallback automático a `DXGI Desktop Duplication` (`IDXGIOutputDuplication`).
   - **Codificación Hardware H.264**: Windows Media Foundation MFT (NVENC / QuickSync / AMF) configurado con CBR a 25 Mbps, 0 B-frames, GOP de 1 segundo y modo `Low-Latency` activo.
   - **Emisor UDP**: Fragmentación RTP directa hacia el puerto `5004` del receptor.
   - **Servidor KVM**: Recibe eventos de entrada en el puerto `5005` y los inyecta en Windows usando `SendInput` de la API de Win32.

3. **`StreamDesk.Receiver` (PC de Escritorio)**:
   - **Receptor UDP**: Socket de alta prioridad con búfer de 8 MB para recibir ráfagas de frames.
   - **Decodificador Hardware**: Media Foundation H.264 Decoder con aceleración por hardware.
   - **Renderizador DirectX 11**: `IDXGISwapChain1` en modo `DXGI_SWAP_EFFECT_FLIP_DISCARD` con `Present(0, 0)` para latencia de presentación nula.
   - **Ventana Interactiva**: Ventana redimensionable fluida, modo Borderless Fullscreen y captura de periféricos.

---

## ⚡ Atajos de Teclado (Receptor)

| Atajo | Acción |
| :--- | :--- |
| **`F11`** | Alternar entre ventana redimensionable y **Pantalla Completa sin bordes (Borderless)**. |
| **`F12`** | **Liberar / Capturar control KVM** (permite mover el cursor de vuelta a la PC principal). |
| **`F3`** | Mostrar / Ocultar HUD de métricas en tiempo real (FPS, Bitrate, Paquetes). |

---

## ⚙️ Puertos de Red Utilizados

Asegúrate de permitir estos puertos en el Firewall de Windows si se solicita:
- **UDP 45454**: Descubrimiento automático en LAN (Broadcast).
- **UDP 5004**: Transmisión de flujo de video H.264 RTP.
- **UDP 5005**: Reenvío de eventos de mouse y teclado (KVM).

---

## 🚀 Compilación y Ejecución

### Requisitos Previos:
- Windows 10 (versión 1903 o superior) o Windows 11.
- SDK de .NET 8 instalado (`dotnet --version` >= 8.0.x).

### 1. Compilar la Solución Completa
Abre una terminal en la carpeta del proyecto y ejecuta:
```powershell
dotnet build -c Release
```

### 2. Ejecutar Pruebas Automatizadas
```powershell
dotnet test
```

### 3. Iniciar el Emisor en la Notebook
```powershell
dotnet run --project StreamDesk.Sender -c Release
```
*Opcional: Si deseas especificar directamente la IP de la PC de escritorio, puedes pasarla como argumento:*
```powershell
dotnet run --project StreamDesk.Sender -c Release -- 192.168.1.50
```

### 4. Iniciar el Receptor en la PC de Escritorio
```powershell
dotnet run --project StreamDesk.Receiver -c Release
```
*La aplicación buscará automáticamente la notebook en la red. Si el broadcast está bloqueado por tu switch, te mostrará un cuadro de diálogo para ingresar la IP directamente.*

---

## 📦 Publicación como Ejecutables Independientes (.exe)

Para generar ejecutables `.exe` portables que no requieran instalar el SDK de .NET en las máquinas:

```powershell
# Publicar Emisor (Notebook)
dotnet publish StreamDesk.Sender -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish/Sender

# Publicar Receptor (PC de Escritorio)
dotnet publish StreamDesk.Receiver -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish/Receiver
```
Los archivos ejecutables quedarán listos en `./publish/Sender/StreamDesk.Sender.exe` y `./publish/Receiver/StreamDesk.Receiver.exe`.
