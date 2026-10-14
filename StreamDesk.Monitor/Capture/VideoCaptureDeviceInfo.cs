using Vortice.MediaFoundation;

namespace StreamDesk.Monitor.Capture;

public record VideoFormatInfo(int Width, int Height, int Fps, Guid Subtype, string FormatName)
{
    public override string ToString() => $"{Width}x{Height} @ {Fps} FPS ({FormatName})";
}

public class VideoCaptureDeviceInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SymbolicLink { get; set; } = string.Empty;
    public IMFActivate Activate { get; set; } = null!;
    public List<VideoFormatInfo> SupportedFormats { get; set; } = new();

    public override string ToString() => Name;
}
