// On-screen notification shown over the game without ever taking focus (the game pauses as soon as it loses focus).
// Visible in borderless / windowed mode; exclusive fullscreen hides other windows (beeps still work).
// The window is opaque on purpose: a semi-transparent (layered) window does not make Windows leave the direct
// "independent flip" presentation used for borderless games, so it stayed invisible on screen.
namespace P5SHighFps.UI;

sealed class Overlay
{
    readonly NotifyForm form = new();

    public Overlay()
    {
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            form.HandleCreated += (_, _) => ready.Set();
            _ = form.Handle;
            Application.Run();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
    }

    public void Show(string text, Color accent, nint gameWindow)
    {
        form.BeginInvoke(() => form.Popup(text, accent, gameWindow));
    }

    sealed class NotifyForm : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 2000 };
        string text = "";
        Color accent = Color.White;

        public NotifyForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 20, 20);
            Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            DoubleBuffered = true;
            hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // TOPMOST | TOOLWINDOW | NOACTIVATE (no LAYERED/TRANSPARENT, see above)
                cp.ExStyle |= 0x00000008 | 0x00000080 | 0x08000000;
                return cp;
            }
        }

        public void Popup(string newText, Color newAccent, nint gameWindow)
        {
            text = newText;
            accent = newAccent;
            var screen = gameWindow != 0 ? Screen.FromHandle(gameWindow) : Screen.PrimaryScreen!;
            var size = TextRenderer.MeasureText(text, Font);
            Size = new Size(size.Width + 60, size.Height + 30);
            Location = new Point(screen.Bounds.Left + (screen.Bounds.Width - Width) / 2, screen.Bounds.Top + 60);
            Invalidate();
            if (!Visible) Show();
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var bar = new SolidBrush(accent);
            e.Graphics.FillRectangle(bar, 0, 0, 8, Height);
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(8, 0, Width - 8, Height), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
