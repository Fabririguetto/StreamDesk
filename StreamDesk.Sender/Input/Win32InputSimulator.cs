namespace StreamDesk.Sender.Input;

/// <summary>
/// Redirige al simulador unificado en StreamDesk.Common.Input.
/// </summary>
public class Win32InputSimulator : StreamDesk.Common.Input.Win32InputSimulator
{
    public Win32InputSimulator(int controlPort = 5005) : base(controlPort)
    {
    }
}
