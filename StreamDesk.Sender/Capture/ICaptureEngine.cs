using Vortice.Direct3D11;

namespace StreamDesk.Sender.Capture;

/// <summary>
/// Contrato unificado para motores de captura de pantalla acelerados por hardware en VRAM.
/// </summary>
public interface ICaptureEngine : IDisposable
{
    event Action<ID3D11Texture2D, long>? OnFrameCaptured;
    bool IsRunning { get; }
    void Start(IntPtr hMonitor, int width, int height);
    void Stop();
}
