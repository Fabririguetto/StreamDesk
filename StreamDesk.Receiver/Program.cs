using StreamDesk.Common.Models;
using StreamDesk.Common.Network;
using StreamDesk.Receiver.UI;

namespace StreamDesk.Receiver;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            MessageBox.Show($"Error fatal en Receiver:\n\n{e.ExceptionObject}", "StreamDesk Receiver - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.ThreadException += (s, e) =>
        {
            MessageBox.Show($"Error de UI en Receiver:\n\n{e.Exception}", "StreamDesk Receiver - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            ApplicationConfiguration.Initialize();

            string? senderIp = null;
            int videoPort = StreamConfig.DefaultVideoPort;
            int controlPort = StreamConfig.DefaultControlPort;
            int streamWidth = 1920;
            int streamHeight = 1080;

            // 1. Si se pasa por línea de comandos (ej: Receiver.exe 192.168.1.45)
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                senderIp = args[0];
            }
            else
            {
                // 2. Intentar descubrimiento automático en LAN vía UDP Broadcast
                using var discovery = new LanDiscoveryService(isSender: false);
                var discoveryTask = discovery.DiscoverSenderAsync(TimeSpan.FromSeconds(2.0));
                var offer = discoveryTask.GetAwaiter().GetResult();

                if (offer != null && !string.IsNullOrEmpty(offer.SenderIp))
                {
                    streamWidth = offer.Width > 0 ? offer.Width : 1920;
                    streamHeight = offer.Height > 0 ? offer.Height : 1080;

                    var choice = MessageBox.Show(
                        $"Se detectó la Notebook emisora en la red:\n\n" +
                        $"Equipo: {offer.SenderName}\n" +
                        $"IP: {offer.SenderIp}\n" +
                        $"Resolución: {streamWidth}x{streamHeight} @ {offer.Fps} FPS\n\n" +
                        $"¿Deseas conectar ahora?",
                        "StreamDesk - Emisor Detectado",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                    if (choice == DialogResult.Yes)
                    {
                        senderIp = offer.SenderIp;
                        videoPort = offer.VideoPort;
                        controlPort = offer.ControlPort;
                    }
                }
            }

            // 3. Fallback: Si no se detectó o se rechazó, solicitar IP manualmente
            if (string.IsNullOrEmpty(senderIp))
            {
                senderIp = PromptForIpAddress();
                if (string.IsNullOrEmpty(senderIp))
                {
                    // Si el usuario cancela o cierra el diálogo, escuchar en broadcast predeterminado en vez de cerrar
                    senderIp = StreamConfig.DefaultBroadcastSubnet;
                }
            }

            Application.Run(new RenderWindow(senderIp, videoPort, controlPort, streamWidth, streamHeight));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fallo al iniciar StreamDesk Receiver:\n\n{ex}", "StreamDesk Receiver - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? PromptForIpAddress()
    {
        var localIps = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
            .Select(ip => ip.ToString())
            .ToList();
        string myIpText = localIps.Count > 0 ? string.Join(", ", localIps) : "Desconocida";

        using var form = new Form
        {
            Width = 440,
            Height = 230,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "StreamDesk - Conectar a Notebook",
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(245, 245, 248)
        };

        var label = new Label
        {
            Left = 25,
            Top = 15,
            Width = 380,
            Height = 45,
            Text = $"IP de esta PC (Receptora): {myIpText}\nIngresa la IP de la Notebook o usa broadcast ({StreamConfig.DefaultBroadcastSubnet}):",
            Font = new Font("Segoe UI", 9.5f)
        };

        var textBox = new TextBox
        {
            Left = 25,
            Top = 70,
            Width = 370,
            Height = 28,
            Text = StreamConfig.DefaultBroadcastSubnet,
            Font = new Font("Segoe UI", 11f)
        };

        var buttonOk = new Button
        {
            Text = "Conectar",
            Left = 200,
            Width = 95,
            Top = 125,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };

        var buttonCancel = new Button
        {
            Text = "Cancelar",
            Left = 305,
            Width = 90,
            Top = 125,
            Height = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat
        };

        form.Controls.Add(label);
        form.Controls.Add(textBox);
        form.Controls.Add(buttonOk);
        form.Controls.Add(buttonCancel);
        form.AcceptButton = buttonOk;
        form.CancelButton = buttonCancel;

        return form.ShowDialog() == DialogResult.OK ? textBox.Text.Trim() : null;
    }
}