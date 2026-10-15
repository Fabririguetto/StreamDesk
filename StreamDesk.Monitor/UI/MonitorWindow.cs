using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Windows.Forms;
using StreamDesk.Common.Models;
using StreamDesk.Common.Network;
using StreamDesk.Common.Power;
using StreamDesk.Monitor.Capture;
using StreamDesk.Monitor.Input;
using StreamDesk.Monitor.Rendering;
using Vortice.Mathematics;

namespace StreamDesk.Monitor.UI;

public class MonitorWindow : Form
{
    private readonly Panel _renderPanel;
    private readonly Panel _toolbarPanel;
    private readonly ComboBox _deviceComboBox;
    private readonly ComboBox _formatComboBox;
    private readonly ComboBox _displayComboBox;
    private readonly TextBox _ipTextBox;
    private readonly Button _connectButton;
    private readonly Button _fullscreenButton;
    private readonly Button _kvmToggleButton;
    private readonly Label _statusLabel;

    private DirectX11MonitorRenderer? _renderer;
    private MediaFoundationDeviceReader? _reader;
    private LowLevelKvmManager? _kvmManager;
    private LanDiscoveryService? _discoveryService;
    private ScreenKeepAwake? _keepAwake;

    private List<VideoCaptureDeviceInfo> _devices = new();
    private bool _isBorderless;
    private FormBorderStyle _prevBorderStyle;
    private FormWindowState _prevWindowState;
    private Rectangle _prevBounds;

    private record DisplayOption(string Name, byte Id)
    {
        public override string ToString() => Name;
    }

    public MonitorWindow(string? defaultIp = null)
    {
        Text = "StreamDesk Monitor - Pantalla Remota & KVM";
        ClientSize = new System.Drawing.Size(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(18, 18, 22);
        KeyPreview = true;

        // Panel de renderizado DirectX
        _renderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(12, 12, 16)
        };
        _renderPanel.MouseDown += OnRenderPanelMouseDown;
        _renderPanel.MouseUp += OnRenderPanelMouseUp;
        _renderPanel.MouseMove += OnRenderPanelMouseMove;
        _renderPanel.MouseWheel += OnRenderPanelMouseWheel;
        _renderPanel.DoubleClick += (s, e) => ToggleBorderless();

        // Barra superior de configuración auto-ocultable
        _toolbarPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 36),
            Padding = new Padding(8, 6, 8, 6)
        };

        var deviceLabel = new Label { Text = "Dispositivo:", AutoSize = true, ForeColor = System.Drawing.Color.LightGray, Top = 12, Left = 8 };
        _deviceComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175, Top = 9, Left = 75, BackColor = System.Drawing.Color.FromArgb(45, 45, 52), ForeColor = System.Drawing.Color.White };
        _deviceComboBox.SelectedIndexChanged += OnDeviceSelected;

        var formatLabel = new Label { Text = "Formato:", AutoSize = true, ForeColor = System.Drawing.Color.LightGray, Top = 12, Left = 258 };
        _formatComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Top = 9, Left = 312, BackColor = System.Drawing.Color.FromArgb(45, 45, 52), ForeColor = System.Drawing.Color.White };

        _connectButton = new Button { Text = "Iniciar Video", Width = 90, Height = 26, Top = 8, Left = 460, BackColor = System.Drawing.Color.FromArgb(0, 122, 204), ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat };
        _connectButton.Click += OnToggleCaptureClicked;

        var ipLabel = new Label { Text = "IP:", AutoSize = true, ForeColor = System.Drawing.Color.LightGray, Top = 12, Left = 558 };
        _ipTextBox = new TextBox { Width = 95, Top = 9, Left = 580, Text = defaultIp ?? StreamConfig.DefaultBroadcastSubnet, BackColor = System.Drawing.Color.FromArgb(45, 45, 52), ForeColor = System.Drawing.Color.White };
        _ipTextBox.TextChanged += (s, e) => _kvmManager?.UpdateTargetIp(_ipTextBox.Text.Trim());

        var displayLabel = new Label { Text = "Monitor (F8):", AutoSize = true, ForeColor = System.Drawing.Color.LightGray, Top = 12, Left = 684 };
        _displayComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, Top = 9, Left = 760, BackColor = System.Drawing.Color.FromArgb(45, 45, 52), ForeColor = System.Drawing.Color.White };
        _displayComboBox.Items.Add(new DisplayOption("Auto (HDMI)", 0));
        _displayComboBox.Items.Add(new DisplayOption("Monitor 1 (Laptop)", 1));
        _displayComboBox.Items.Add(new DisplayOption("Monitor 2 (HDMI)", 2));
        _displayComboBox.Items.Add(new DisplayOption("Todo el Escritorio", 255));
        _displayComboBox.SelectedIndex = 0;
        _displayComboBox.SelectedIndexChanged += (s, e) =>
        {
            if (_kvmManager != null && _displayComboBox.SelectedItem is DisplayOption opt)
            {
                _kvmManager.TargetDisplay = opt.Id;
            }
        };

        _kvmToggleButton = new Button
        {
            Text = "🟢 KVM: ON (F12)",
            Width = 125,
            Height = 26,
            Top = 8,
            Left = 915,
            BackColor = System.Drawing.Color.FromArgb(46, 125, 50),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold)
        };
        _kvmToggleButton.Click += (s, e) => ToggleKvmControl();

        _statusLabel = new Label
        {
            Text = "Activo",
            AutoSize = true,
            Top = 13,
            Left = 1048,
            ForeColor = System.Drawing.Color.LimeGreen,
            Font = new Font(Font, FontStyle.Regular)
        };

        _fullscreenButton = new Button { Text = "⛶ Fullscreen (F11)", Width = 115, Height = 26, Top = 8, Left = 1150, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = System.Drawing.Color.FromArgb(50, 50, 60), ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat };
        _fullscreenButton.Click += (s, e) => ToggleBorderless();

        _toolbarPanel.Controls.AddRange([deviceLabel, _deviceComboBox, formatLabel, _formatComboBox, _connectButton, ipLabel, _ipTextBox, displayLabel, _displayComboBox, _kvmToggleButton, _statusLabel, _fullscreenButton]);

        Controls.Add(_renderPanel);
        Controls.Add(_toolbarPanel);

        KeyDown += OnWindowKeyDown;
        Resize += OnWindowResize;

        // Inicializar renderizador D3D11
        Shown += (s, e) =>
        {
            _renderer = new DirectX11MonitorRenderer(_renderPanel.Handle, _renderPanel.Width, _renderPanel.Height);
            _renderer.ClearScreen(new Color4(0.05f, 0.05f, 0.07f, 1.0f));

            InitializeKvm();
            RefreshDeviceList();
            StartDiscovery();
        };
    }

    private void InitializeKvm()
    {
        string targetIp = _ipTextBox.Text.Trim();
        _kvmManager = new LowLevelKvmManager(targetIp, StreamConfig.DefaultControlPort);
        if (_displayComboBox.SelectedItem is DisplayOption opt)
        {
            _kvmManager.TargetDisplay = opt.Id;
        }
        _kvmManager.CanSendKeyboard = () => !_ipTextBox.Focused;
        _kvmManager.OnKvmStateChanged += enabled =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateKvmUi(enabled));
            }
            else
            {
                UpdateKvmUi(enabled);
            }
        };
    }

    private void ToggleKvmControl()
    {
        _kvmManager?.ToggleControl();
    }

    private void UpdateKvmUi(bool enabled)
    {
        if (enabled)
        {
            _kvmToggleButton.Text = "🟢 KVM: ACTIVO (F12)";
            _kvmToggleButton.BackColor = System.Drawing.Color.FromArgb(46, 125, 50);
            _statusLabel.Text = "Control activo";
            _statusLabel.ForeColor = System.Drawing.Color.LimeGreen;
        }
        else
        {
            _kvmToggleButton.Text = "🔴 KVM: PAUSADO (F12)";
            _kvmToggleButton.BackColor = System.Drawing.Color.FromArgb(180, 40, 40);
            _statusLabel.Text = "Control pausado (solo visor)";
            _statusLabel.ForeColor = System.Drawing.Color.IndianRed;
        }
    }

    private void RefreshDeviceList()
    {
        _devices = VideoCaptureDeviceEnumerator.GetConnectedDevices();
        _deviceComboBox.Items.Clear();

        if (_devices.Count == 0)
        {
            _deviceComboBox.Items.Add("No se detectaron capturadoras");
            _deviceComboBox.SelectedIndex = 0;
            _connectButton.Enabled = false;
            return;
        }

        foreach (var dev in _devices)
        {
            _deviceComboBox.Items.Add(dev.Name);
        }

        _connectButton.Enabled = true;
        _deviceComboBox.SelectedIndex = 0;
    }

    private void OnDeviceSelected(object? sender, EventArgs e)
    {
        int idx = _deviceComboBox.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count) return;

        var selectedDevice = _devices[idx];
        _formatComboBox.Items.Clear();

        foreach (var fmt in selectedDevice.SupportedFormats)
        {
            _formatComboBox.Items.Add(fmt);
        }

        if (_formatComboBox.Items.Count > 0)
        {
            _formatComboBox.SelectedIndex = 0;
        }
    }

    private void OnToggleCaptureClicked(object? sender, EventArgs e)
    {
        if (_reader != null && _reader.IsRunning)
        {
            StopCapture();
            _connectButton.Text = "Iniciar Video";
            _connectButton.BackColor = System.Drawing.Color.FromArgb(0, 122, 204);
        }
        else
        {
            StartCapture();
            if (_reader != null && _reader.IsRunning)
            {
                _connectButton.Text = "Detener Video";
                _connectButton.BackColor = System.Drawing.Color.FromArgb(180, 40, 40);
            }
        }
    }

    private void StartCapture()
    {
        int idx = _deviceComboBox.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count) return;

        var device = _devices[idx];
        var format = _formatComboBox.SelectedItem as VideoFormatInfo;

        try
        {
            _reader?.Dispose();
            _reader = new MediaFoundationDeviceReader(device, format);
            _reader.OnFrameArrived += (pData, length, width, height, stride) =>
            {
                _renderer?.RenderFrame(pData, length, width, height, stride);
            };
            _reader.Start();
            _keepAwake ??= new ScreenKeepAwake();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al iniciar la capturadora:\n\n{ex.Message}", "Error de Video", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopCapture()
    {
        _reader?.Stop();
        _reader?.Dispose();
        _reader = null;
        _keepAwake?.Dispose();
        _keepAwake = null;
        _renderer?.ClearScreen(new Color4(0.05f, 0.05f, 0.07f, 1.0f));
    }

    private void StartDiscovery()
    {
        _discoveryService = new LanDiscoveryService(isSender: false);
        Task.Run(async () =>
        {
            var offer = await _discoveryService.DiscoverSenderAsync(TimeSpan.FromSeconds(3.0));
            if (offer != null && !string.IsNullOrEmpty(offer.SenderIp))
            {
                if (InvokeRequired)
                {
                    BeginInvoke(() =>
                    {
                        _ipTextBox.Text = offer.SenderIp;
                        _kvmManager?.UpdateTargetIp(offer.SenderIp, StreamConfig.DefaultControlPort);
                    });
                }
            }
        });
    }

    private void OnRenderPanelMouseDown(object? sender, MouseEventArgs e)
    {
        if (_kvmManager == null) return;

        _renderPanel.Focus();

        var btn = e.Button switch
        {
            MouseButtons.Left => MouseButtonType.Left,
            MouseButtons.Right => MouseButtonType.Right,
            MouseButtons.Middle => MouseButtonType.Middle,
            _ => MouseButtonType.None
        };
        _kvmManager.SendMouseButton(ControlPacketType.MouseDown, btn, e.X, e.Y, _renderPanel.Width, _renderPanel.Height);
    }

    private void OnRenderPanelMouseUp(object? sender, MouseEventArgs e)
    {
        if (_kvmManager == null) return;

        var btn = e.Button switch
        {
            MouseButtons.Left => MouseButtonType.Left,
            MouseButtons.Right => MouseButtonType.Right,
            MouseButtons.Middle => MouseButtonType.Middle,
            _ => MouseButtonType.None
        };
        _kvmManager.SendMouseButton(ControlPacketType.MouseUp, btn, e.X, e.Y, _renderPanel.Width, _renderPanel.Height);
    }

    private void OnRenderPanelMouseMove(object? sender, MouseEventArgs e)
    {
        _kvmManager?.SendMouseMove(e.X, e.Y, _renderPanel.Width, _renderPanel.Height);

        // Auto-mostrar toolbar cuando el mouse está en el borde superior en modo fullscreen
        if (_isBorderless && e.Y < 10 && !_toolbarPanel.Visible)
        {
            _toolbarPanel.Visible = true;
        }
    }

    private void OnRenderPanelMouseWheel(object? sender, MouseEventArgs e)
    {
        _kvmManager?.SendMouseWheel(e.Delta);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F8)
        {
            CycleTargetDisplay();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F11)
        {
            ToggleBorderless();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F12)
        {
            ToggleKvmControl();
            e.Handled = true;
        }
    }

    private void CycleTargetDisplay()
    {
        if (_displayComboBox.Items.Count > 0)
        {
            int next = (_displayComboBox.SelectedIndex + 1) % _displayComboBox.Items.Count;
            _displayComboBox.SelectedIndex = next;
        }
    }

    private void OnWindowResize(object? sender, EventArgs e)
    {
    }

    private void ToggleBorderless()
    {
        if (!_isBorderless)
        {
            _prevWindowState = WindowState;
            _prevBorderStyle = FormBorderStyle;
            _prevBounds = Bounds;

            _toolbarPanel.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _isBorderless = true;
        }
        else
        {
            _toolbarPanel.Visible = true;
            FormBorderStyle = _prevBorderStyle;
            Bounds = _prevBounds;
            WindowState = _prevWindowState;
            _isBorderless = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopCapture();
            _keepAwake?.Dispose();
            _kvmManager?.Dispose();
            _renderer?.Dispose();
            _discoveryService?.Dispose();
        }
        base.Dispose(disposing);
    }
}
