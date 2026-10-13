using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamDesk.Common.Models;

namespace StreamDesk.Common.Network;

/// <summary>
/// Servicio de descubrimiento Zero-Config sobre LAN Ethernet usando UDP Broadcast.
/// </summary>
public class LanDiscoveryService : IDisposable
{
    private readonly UdpClient _udpClient;
    private bool _disposed;

    public LanDiscoveryService(bool isSender, int port = StreamConfig.DefaultDiscoveryPort)
    {
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (isSender)
        {
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
    }

    /// <summary>
    /// Modo Sender: Escucha peticiones de descubrimiento y responde con sus parámetros de streaming.
    /// </summary>
    public async Task StartSenderListenerAsync(DiscoveryResponse senderInfo, CancellationToken ct, Action<IPEndPoint>? onReceiverDiscovered = null)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                string message = Encoding.UTF8.GetString(result.Buffer);

                if (message.Contains("DISCOVER_SENDER"))
                {
                    // Determinar la IP local visible en la red
                    senderInfo.SenderIp = GetLocalIpAddressForEndpoint(result.RemoteEndPoint);
                    string responseJson = JsonSerializer.Serialize(senderInfo);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);

                    await _udpClient.SendAsync(responseBytes, responseBytes.Length, result.RemoteEndPoint);

                    onReceiverDiscovered?.Invoke(result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_disposed) break;
                Console.WriteLine($"[Discovery] Error en listener del emisor: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
    }

    /// <summary>
    /// Modo Receiver: Emite una búsqueda broadcast y espera la respuesta del Sender.
    /// </summary>
    public async Task<DiscoveryResponse?> DiscoverSenderAsync(TimeSpan timeout, int port = StreamConfig.DefaultDiscoveryPort)
    {
        using var cts = new CancellationTokenSource(timeout);
        var request = new DiscoveryRequest();
        byte[] requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));

        try
        {
            // Enviar broadcast a la subred local
            await _udpClient.SendAsync(requestBytes, requestBytes.Length, new IPEndPoint(IPAddress.Broadcast, port));

            var result = await _udpClient.ReceiveAsync(cts.Token);
            string json = Encoding.UTF8.GetString(result.Buffer);
            var response = JsonSerializer.Deserialize<DiscoveryResponse>(json);

            if (response != null && string.IsNullOrEmpty(response.SenderIp))
            {
                response.SenderIp = result.RemoteEndPoint.Address.ToString();
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            return null; // Timeout sin respuestas
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] Excepción durante la búsqueda: {ex.Message}");
            return null;
        }
    }

    private static string GetLocalIpAddressForEndpoint(IPEndPoint remoteEndpoint)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect(remoteEndpoint.Address, remoteEndpoint.Port);
            if (socket.LocalEndPoint is IPEndPoint localEndpoint)
            {
                return localEndpoint.Address.ToString();
            }
        }
        catch
        {
            // Fallback
        }

        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            {
                return ip.ToString();
            }
        }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _udpClient.Close();
        _udpClient.Dispose();
    }
}
