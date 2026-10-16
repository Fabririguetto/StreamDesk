using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StreamDesk.Common.Input;

namespace StreamDesk.Agent.Mirroring;

/// <summary>
/// Servicio de proyección de pantallas en tiempo real para la Notebook.
/// Permite conmutar la salida HDMI entre:
/// - Modo 1: Monitor 1 (Laptop en pantalla completa sobre HDMI)
/// - Modo 2: Monitor 2 (Escritorio nativo de salida HDMI)
/// - Modo 3: Dual (Pantalla 1 en la mitad izquierda + Pantalla 2 en la mitad derecha)
/// Utiliza aceleración por hardware DWM vía Win32 sin consumo de red.
/// </summary>
public class DisplayMirrorService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest,
        IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc,
        uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int nStretchMode);

    private const int COLORONCOLOR = 3;
    private const uint SRCCOPY = 0x00CC0020;

    private readonly Win32InputSimulator _simulator;
    private readonly Thread _uiThread;
    private readonly Thread _renderThread;
    private ProjectionForm? _form;
    private volatile bool _isRunning = true;
    private bool _disposed;

    private byte _currentMode = 2; // Default: Modo 2 (HDMI nativo sin proyeccion)
    private Rectangle _primaryBounds;
    private Rectangle _secondaryBounds;
    private bool _hasSecondary;

    public DisplayMirrorService(Win32InputSimulator simulator)
    {
        _simulator = simulator;
        RefreshMonitorBounds();

        _simulator.OnDisplayModeChanged += mode => SetMode(mode);

        _uiThread = new Thread(UiThreadStart)
        {
            Name = "Display Mirror UI Thread",
            IsBackground = true
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        _renderThread = new Thread(RenderLoop)
        {
            Name = "Display Mirror Render Loop",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _renderThread.Start();
    }

    private void RefreshMonitorBounds()
    {
        var primary = _simulator.Displays.FirstOrDefault(d => d.IsPrimary) ?? _simulator.Displays.FirstOrDefault();
        var secondary = _simulator.Displays.FirstOrDefault(d => !d.IsPrimary);

        if (primary != null)
        {
            _primaryBounds = new Rectangle(primary.Bounds.Left, primary.Bounds.Top, primary.Bounds.Width, primary.Bounds.Height);
        }
        else
        {
            _primaryBounds = new Rectangle(0, 0, 1920, 1080);
        }

        if (secondary != null)
        {
            _secondaryBounds = new Rectangle(secondary.Bounds.Left, secondary.Bounds.Top, secondary.Bounds.Width, secondary.Bounds.Height);
            _hasSecondary = true;
        }
        else
        {
            _hasSecondary = false;
        }
    }

    public void SetMode(byte mode)
    {
        _currentMode = mode;
        if (_form != null && _form.IsHandleCreated)
        {
            _form.BeginInvoke(() => ApplyModeToForm());
        }
    }

    private void UiThreadStart()
    {
        _form = new ProjectionForm();
        _form.Load += (s, e) => ApplyModeToForm();
        Application.Run(_form);
    }

    private void ApplyModeToForm()
    {
        if (_form == null || !_form.IsHandleCreated) return;

        RefreshMonitorBounds();

        if (!_hasSecondary)
        {
            // Solo hay 1 monitor: no proyectar sobre sí mismo
            _form.Visible = false;
            return;
        }

        switch (_currentMode)
        {
            case 1: // Monitor 1 (Laptop full sobre HDMI)
                _form.SetBounds(_secondaryBounds.Left, _secondaryBounds.Top, _secondaryBounds.Width, _secondaryBounds.Height);
                _form.Visible = true;
                _form.BringToFront();
                Console.WriteLine("[Proyector] Modo 1 activo: Proyectando Pantalla 1 en pantalla completa sobre HDMI.");
                break;

            case 3: // Dual (Lado a Lado: mitad izquierda Pantalla 1, mitad derecha Pantalla 2)
                int halfWidth = _secondaryBounds.Width / 2;
                _form.SetBounds(_secondaryBounds.Left, _secondaryBounds.Top, halfWidth, _secondaryBounds.Height);
                _form.Visible = true;
                _form.BringToFront();
                Console.WriteLine("[Proyector] Modo 3 activo: Dual Lado a Lado (50% Pantalla 1 / 50% Pantalla 2).");
                break;

            case 2: // Monitor 2 (HDMI nativo sin overlay)
            default:
                _form.Visible = false;
                Console.WriteLine("[Proyector] Modo 2 activo: Mostrando salida nativa HDMI (sin proyector).");
                break;
        }
    }

    private void RenderLoop()
    {
        while (_isRunning && !_disposed)
        {
            try
            {
                if (_hasSecondary && (_currentMode == 1 || _currentMode == 3) && _form != null && _form.IsHandleCreated && _form.Visible)
                {
                    IntPtr hwnd = _form.Handle;
                    int destW = _form.ClientSize.Width;
                    int destH = _form.ClientSize.Height;

                    if (destW > 0 && destH > 0)
                    {
                        IntPtr hdcDest = GetDC(hwnd);
                        if (hdcDest != IntPtr.Zero)
                        {
                            IntPtr hdcSrc = GetDC(IntPtr.Zero);
                            if (hdcSrc != IntPtr.Zero)
                            {
                                SetStretchBltMode(hdcDest, COLORONCOLOR);
                                StretchBlt(
                                    hdcDest, 0, 0, destW, destH,
                                    hdcSrc, _primaryBounds.Left, _primaryBounds.Top, _primaryBounds.Width, _primaryBounds.Height,
                                    SRCCOPY);

                                ReleaseDC(IntPtr.Zero, hdcSrc);
                            }
                            ReleaseDC(hwnd, hdcDest);
                        }
                    }

                    // ~60 FPS
                    Thread.Sleep(16);
                }
                else
                {
                    // En modo nativo o inactivo, dormir más para 0% consumo
                    Thread.Sleep(100);
                }
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isRunning = false;

        if (_form != null && _form.IsHandleCreated)
        {
            try
            {
                _form.BeginInvoke(() =>
                {
                    _form.Close();
                    _form.Dispose();
                });
            }
            catch { }
        }
    }

    private class ProjectionForm : Form
    {
        public ProjectionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (Ocultar de Alt+Tab)
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (No robar foco)
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // No pintar fondo para evitar parpadeos (flicker-free)
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
