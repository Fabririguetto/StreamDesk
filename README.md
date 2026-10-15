# StreamDesk 🚀
### Solución Hardware + LAN KVM de Cero Latencia y 0% de CPU para Windows

**StreamDesk** es una suite de alto rendimiento desarrollada en **C# (.NET 8)** para controlar una **Notebook** desde una **PC de Escritorio**:
- **Video:** Flujo de video directo sin compresión por hardware conectando la salida HDMI de la notebook a una capturadora USB/PCIe en la PC de escritorio.
- **Mouse y Teclado (KVM):** Reenvío de periféricos en tiempo real sobre red local Ethernet/Wi-Fi mediante paquetes binarios compactos UDP de 16 bytes.
- **Multimonitor y Escritorio Extendido:** Detección de monitores conectados y mapeo de coordenadas sobre el Virtual Desktop de Windows.
- **Sin permisos de Administrador:** Sistema de actividad y anti-bloqueo activo que previene la suspensión o bloqueo de la notebook sin requerir servicios ni privilegios elevados.

---

## 🏗️ Arquitectura del Sistema

La solución está compuesta por:

1. **`StreamDesk.Common`**:
   - Biblioteca compartida con contratos de red y serialización binaria.
   - **`Win32InputSimulator`**: Inyección de periféricos con `SendInput`, mapeo de monitores múltiples (`EnumDisplayMonitors`, `MONITORINFOEX`) y escalado al Virtual Desktop.
   - **`ScreenKeepAwake`**: Prevención activa de bloqueo de sesión y suspensión sin derechos de administrador mediante `SetThreadExecutionState`, micro-jiggle de mouse y pulso `VK_F15`.
   - **`ControlPacket`**: Estructura binaria de 16 bytes para reenvío instantáneo de mouse, rueda y teclado.
   - **`LanDiscoveryService`**: Detección automática Zero-Config entre las computadoras vía UDP Broadcast (puerto `45454`).

2. **`StreamDesk.Monitor` (PC de Escritorio / Host)**:
   - Captura directa de video desde la capturadora mediante **Windows Media Foundation** (YUY2, NV12 o MJPEG).
   - Renderizado con **DirectX 11** (`IDXGISwapChain1`) con latencia nula de presentación (`Present(0, 0)`).
   - Gancho global de bajo nivel (`SetWindowsHookEx`) para teclado y reenvío de mouse sin captura restrictiva de cursor (`ClipCursor` deshabilitado).
   - Interruptor de KVM por botón en pantalla y hotkey global.
   - Selector de monitor objetivo (Auto, Laptop, HDMI, Escritorio Extendido) y hotkey para conmutar.

3. **`StreamDesk.Agent` (Notebook / Cliente)**:
   - Agente ultraligero con **0.0% de consumo de CPU/GPU**.
   - No codifica video por software (el video sale por HDMI).
   - Escucha en el puerto UDP `5005` e inyecta las entradas en la pantalla seleccionada.
   - Configura automáticamente el perfil de energía para permitir cerrar la tapa de la laptop sin suspender (`LIDACTION 0`).

---

## ⚡ Atajos de Teclado (PC de Escritorio)

| Atajo | Acción |
| :--- | :--- |
| **`F12`** | **Pausar / Reanudar control KVM** (libera el control para usar la PC local). |
| **`F11`** | Alternar entre ventana y **Pantalla Completa sin bordes (Borderless)**. |
| **`F8`** | **Alternar Monitor Objetivo** (Auto HDMI / Pantalla 1 / Pantalla 2 / Escritorio Completo). |

---

## ⚙️ Puertos de Red Utilizados

Asegúrate de permitir estos puertos en el Firewall de Windows si se solicita:
- **UDP 45454**: Descubrimiento automático en LAN (Broadcast).
- **UDP 5005**: Reenvío de eventos de mouse y teclado (KVM).

---

## 🚀 Compilación y Ejecución

### Requisitos Previos:
- Windows 10 (versión 1903 o superior) o Windows 11.
- SDK de .NET 8 instalado (`dotnet --version` >= 8.0.x).
- Capturadora de video HDMI (USB 3.0 Cam Link o PCIe) conectada a la PC de escritorio.

### 1. Compilar la Solución Completa
```powershell
dotnet build -c Release
```

### 2. Ejecutar Pruebas Automatizadas
```powershell
dotnet test
```

### 3. Iniciar el Agente en la Notebook
Ejecuta el script incluido:
```cmd
run-agent.bat
```
O directamente con dotnet:
```powershell
dotnet run --project StreamDesk.Agent -c Release
```

### 4. Iniciar el Monitor en la PC de Escritorio
Ejecuta el script incluido:
```cmd
run-monitor.bat
```
O directamente con dotnet:
```powershell
dotnet run --project StreamDesk.Monitor -c Release
```

---

## 📦 Publicación como Ejecutables Independientes (.exe)

Para generar ejecutables `.exe` portables autocontenidos (que no requieren tener .NET instalado en las máquinas):

Ejecuta el script:
```cmd
publish.bat
```

Los binarios quedarán listos en:
- `publish\Monitor\StreamDesk.Monitor.exe` (PC de Escritorio)
- `publish\Agent\StreamDesk.Agent.exe` (Notebook)
