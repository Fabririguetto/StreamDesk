using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace StreamDesk.Monitor.Capture;

public static class VideoCaptureDeviceEnumerator
{
    private static readonly Guid GuidSourceType = CaptureDeviceAttributeKeys.SourceType;
    private static readonly Guid GuidSourceTypeVidcap = CaptureDeviceAttributeKeys.SourceTypeVidcap;
    private static readonly Guid GuidFriendlyName = CaptureDeviceAttributeKeys.FriendlyName;
    private static readonly Guid GuidSymbolicLink = CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink;

    public static List<VideoCaptureDeviceInfo> GetConnectedDevices()
    {
        MediaFactory.MFStartup(true);
        var resultList = new List<VideoCaptureDeviceInfo>();

        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(GuidSourceType, GuidSourceTypeVidcap);

            using var devices = MediaFactory.MFEnumDeviceSources(attributes);
            int index = 0;

            foreach (var activate in devices)
            {
                try
                {
                    string name = activate.GetString(GuidFriendlyName);
                    string link = string.Empty;
                    try { link = activate.GetString(GuidSymbolicLink); } catch { }

                    var devInfo = new VideoCaptureDeviceInfo
                    {
                        Index = index++,
                        Name = string.IsNullOrWhiteSpace(name) ? $"Dispositivo de Captura #{index}" : name,
                        SymbolicLink = link,
                        Activate = activate
                    };

                    // Obtener resoluciones y formatos soportados
                    PopulateFormats(devInfo);
                    resultList.Add(devInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EnumDevices] Error leyendo dispositivo: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EnumDevices] Error enumerando dispositivos: {ex.Message}");
        }

        return resultList;
    }

    private static void PopulateFormats(VideoCaptureDeviceInfo devInfo)
    {
        try
        {
            var mediaSource = devInfo.Activate.ActivateObject<IMFMediaSource>();
            if (mediaSource == null) return;

            using (mediaSource)
            using (var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(mediaSource, null))
            {
                var seen = new HashSet<string>();

                for (int i = 0; ; i++)
                {
                    IMFMediaType? nativeType = null;
                    try
                    {
                        nativeType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i);
                    }
                    catch
                    {
                        break;
                    }

                    if (nativeType == null) break;

                    using (nativeType)
                    {
                        MediaFactory.MFGetAttributeSize(nativeType, MediaTypeAttributeKeys.FrameSize, out uint width, out uint height);
                        MediaFactory.MFGetAttributeRatio(nativeType, MediaTypeAttributeKeys.FrameRate, out uint fpsNum, out uint fpsDen);
                        int fps = fpsDen > 0 ? (int)Math.Round((double)fpsNum / fpsDen) : 30;

                        var subtype = nativeType.GetGUID(MediaTypeAttributeKeys.Subtype);
                        string formatName = GetSubtypeName(subtype);

                        string key = $"{width}x{height}@{fps}@{formatName}";
                        if (width > 0 && height > 0 && seen.Add(key))
                        {
                            devInfo.SupportedFormats.Add(new VideoFormatInfo((int)width, (int)height, fps, subtype, formatName));
                        }
                    }
                }
            }

            // Ordenar por resolución y luego FPS descendente
            devInfo.SupportedFormats = devInfo.SupportedFormats
                .OrderByDescending(f => f.Width * f.Height)
                .ThenByDescending(f => f.Fps)
                .ToList();
        }
        catch
        {
            // Fallback con formatos estándar si el dispositivo no permite enumerar formatos pasivamente
            devInfo.SupportedFormats.Add(new VideoFormatInfo(1920, 1080, 60, VideoFormatGuids.NV12, "NV12"));
            devInfo.SupportedFormats.Add(new VideoFormatInfo(1920, 1080, 30, VideoFormatGuids.NV12, "NV12"));
            devInfo.SupportedFormats.Add(new VideoFormatInfo(1280, 720, 60, VideoFormatGuids.NV12, "NV12"));
        }
    }

    private static string GetSubtypeName(Guid subtype)
    {
        if (subtype == VideoFormatGuids.NV12) return "NV12";
        if (subtype == VideoFormatGuids.YUY2) return "YUY2";
        if (subtype == VideoFormatGuids.Mjpg) return "MJPEG";
        if (subtype == VideoFormatGuids.Rgb32) return "RGB32";
        if (subtype == VideoFormatGuids.Rgb24) return "RGB24";
        return subtype.ToString()[..8];
    }
}
