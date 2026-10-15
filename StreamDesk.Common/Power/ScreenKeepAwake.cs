using System.Runtime.InteropServices;

namespace StreamDesk.Common.Power;

/// <summary>
/// Previene que Windows apague la pantalla, active el protector de pantalla,
/// bloquee la sesión por inactividad o entre en suspensión mientras StreamDesk esté activo.
/// </summary>
public sealed class ScreenKeepAwake : IDisposable
{
    [Flags]
    public enum ExecutionState : uint
    {
        ES_SYSTEM_REQUIRED   = 0x00000001,
        ES_DISPLAY_REQUIRED  = 0x00000002,
        ES_USER_PRESENT      = 0x00000004,
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS        = 0x80000000,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_MOVE = 0x0001;

    private readonly Timer? _heartbeatTimer;
    private bool _isActive;
    private bool _disposed;

    public bool IsActive => _isActive;

    /// <summary>
    /// Inicializa la prevención de suspensión y bloqueo de pantalla.
    /// </summary>
    /// <param name="autoStart">Si es true, activa la prevención inmediatamente.</param>
    /// <param name="heartbeatIntervalSeconds">Intervalo en segundos para refrescar el temporizador de inactividad de Windows (predeterminado 30s).</param>
    public ScreenKeepAwake(bool autoStart = true, int heartbeatIntervalSeconds = 30)
    {
        if (autoStart)
        {
            Enable();
        }

        if (heartbeatIntervalSeconds > 0)
        {
            _heartbeatTimer = new Timer(
                _ => Heartbeat(),
                null,
                TimeSpan.FromSeconds(heartbeatIntervalSeconds),
                TimeSpan.FromSeconds(heartbeatIntervalSeconds)
            );
        }
    }

    /// <summary>
    /// Activa el estado para evitar suspensión y apagado/bloqueo de pantalla.
    /// </summary>
    public void Enable()
    {
        if (_disposed) return;
        _isActive = true;
        SetThreadExecutionState(ExecutionState.ES_CONTINUOUS | ExecutionState.ES_DISPLAY_REQUIRED | ExecutionState.ES_SYSTEM_REQUIRED | ExecutionState.ES_USER_PRESENT);
        // Simular un movimiento delta 0,0 para reiniciar inmediatamente el temporizador GetLastInputInfo de Windows
        // evitando que se dispare la pantalla de bloqueo o protector de pantalla.
        mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// Restaura la configuración normal de energía de Windows.
    /// </summary>
    public void Disable()
    {
        if (_disposed) return;
        _isActive = false;
        SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
    }

    private void Heartbeat()
    {
        if (_isActive && !_disposed)
        {
            // Renovar periódicamente los flags para reiniciar el temporizador de inactividad de energía
            SetThreadExecutionState(ExecutionState.ES_DISPLAY_REQUIRED | ExecutionState.ES_SYSTEM_REQUIRED | ExecutionState.ES_USER_PRESENT);
            // Simular evento neutro de usuario para reiniciar GetLastInputInfo y evitar el bloqueo por inactividad
            mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeatTimer?.Dispose();
        Disable();
    }
}
