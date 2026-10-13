using System.Text.Json.Serialization;

namespace StreamDesk.Common.Network;

/// <summary>
/// Solicitud de descubrimiento emitida por el Receiver hacia la subred LAN vía broadcast UDP.
/// </summary>
public class DiscoveryRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "DISCOVER_SENDER";

    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = Environment.MachineName;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "1.0.0";
}

/// <summary>
/// Anuncio y oferta de streaming emitida por el Sender en respuesta a una solicitud de descubrimiento.
/// </summary>
public class DiscoveryResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "SENDER_ANNOUNCE";

    [JsonPropertyName("senderName")]
    public string SenderName { get; set; } = Environment.MachineName;

    [JsonPropertyName("videoPort")]
    public int VideoPort { get; set; } = 5004;

    [JsonPropertyName("controlPort")]
    public int ControlPort { get; set; } = 5005;

    [JsonPropertyName("width")]
    public int Width { get; set; } = 1920;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1080;

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 60;

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "H264";

    [JsonPropertyName("senderIp")]
    public string SenderIp { get; set; } = string.Empty;
}
