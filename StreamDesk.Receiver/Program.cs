using StreamDesk.Common.Models;
using StreamDesk.Common.Network;
using StreamDesk.Receiver.UI;

namespace StreamDesk.Receiver;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? senderIp = null;
        int videoPort = StreamConfig.DefaultVideoPort;
        int controlPort = StreamConfig.DefaultControlPort;

        // 1. Si se pasa por línea de comandos (ej: Receiver.exe 192.168.1.45)
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            senderIp = args[0];
        }
        else
        {
            // 2. Intentar descubrimiento automático en LAN vía UDP Broadcast
            using var discovery = new LanDiscoveryService(isSender: false);
            var discoveryTask = discovery.DiscoverSenderAsync(TimeSpan.FromSeconds(2.5));
            var offer = discoveryTask.GetAwaiter().GetResult();

            if (offer != null && !string.IsNullOrEmpty(offer.SenderIp))
            {
                var choice = MessageBox.Show(
                    $"Se detectó la Notebook emisora en la red:\n\n" +
                    $"Equipo: {offer.SenderName}\n" +
                    $"IP: {offer.SenderIp}\n" +
                    $"Resolución: {offer.Width}x{offer.Height} @ {offer.Fps} FPS\n\n" +
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
                return; // Usuario canceló
            }
        }

        Application.Run(new RenderWindow(senderIp, videoPort, controlPort));
    }

    private static string? PromptForIpAddress()
    {
        using var form = new Form
        {
            Width = 420,
            Height = 210,
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
            Top = 20,
            Width = 350,
            Height = 35,
            Text = "Ingresa la dirección IP de la Notebook emisora en tu red local (Ethernet):",
            Font = new Font("Segoe UI", 9.5f)
        };

        var textBox = new TextBox
        {
            Left = 25,
            Top = 65,
            Width = 350,
            Height = 28,
            Text = StreamConfig.DefaultBroadcastSubnet,
            Font = new Font("Segoe UI", 11f)
        };

        var buttonOk = new Button
        {
            Text = "Conectar",
            Left = 190,
            Width = 95,
            Top = 115,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };

        var buttonCancel = new Button
        {
            Text = "Cancelar",
            Left = 295,
            Width = 80,
            Top = 115,
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