using System.Runtime.InteropServices;

namespace MicroRecord;

internal enum ToastLevel { Info, Success, Warning, Error }

/// <summary>
/// A small in-app notification shown instantly in the bottom-right corner, instead of a Windows
/// balloon/toast (those can arrive seconds late or be silenced by Focus Assist). It never steals
/// focus, auto-dismisses, closes on click, and stacks upward when several are shown.
/// Sizes are in logical units and scaled by the monitor DPI so text stays large and crisp.
/// </summary>
internal sealed class Toast : Form
{
    private const int MarginDip = 14;
    private const int GapDip = 10;
    private const int WidthDip = 380;
    private static readonly List<Toast> Open = new();

    private readonly System.Windows.Forms.Timer life = new();
    private readonly Label messageLabel;

    private Toast(string title, string message, ToastLevel level, int durationMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None; // we scale by DeviceDpi ourselves, in OnLoad
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(32, 34, 40);
        Padding = new Padding(0);
        DoubleBuffered = true;

        var accent = level switch
        {
            ToastLevel.Success => Color.FromArgb(63, 185, 80),
            ToastLevel.Warning => Color.FromArgb(230, 170, 40),
            ToastLevel.Error => Color.FromArgb(230, 70, 66),
            _ => Color.FromArgb(70, 150, 250)
        };

        var family = SystemFonts.MessageBoxFont!.FontFamily;
        var titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font(family, 12.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
            UseCompatibleTextRendering = false
        };
        messageLabel = new Label
        {
            Text = message,
            AutoSize = true,
            ForeColor = Color.FromArgb(222, 226, 232),
            Font = new Font(family, 11f),
            Margin = new Padding(0),
            UseCompatibleTextRendering = false
        };

        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill
        };
        text.Controls.Add(titleLabel);
        text.Controls.Add(messageLabel);

        var stripe = new Panel { Dock = DockStyle.Left, BackColor = accent };

        var container = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        container.Controls.Add(text);
        container.Controls.Add(stripe);

        Controls.Add(container);
        this.stripe = stripe;
        this.text = text;

        foreach (var c in new Control[] { this, container, stripe, text, titleLabel, messageLabel })
            c.Click += (_, _) => Close();

        life.Interval = Math.Max(1000, durationMs);
        life.Tick += (_, _) => Close();
    }

    private readonly Panel stripe;
    private readonly FlowLayoutPanel text;

    private int Dip(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        stripe.Width = Dip(5);
        var pad = Dip(14);
        text.Padding = new Padding(pad, pad, pad, pad);
        messageLabel.MaximumSize = new Size(Dip(WidthDip) - stripe.Width - pad * 2, 0);
        MinimumSize = new Size(Dip(WidthDip), 0);
        Reposition();
    }

    /// <summary>Shows a toast. Must be called on the UI thread.</summary>
    public static void Show(string title, string message, ToastLevel level = ToastLevel.Info, int durationMs = 3500)
    {
        var toast = new Toast(title, message, level, durationMs);
        Open.Add(toast);
        toast.Show();
        toast.life.Start();
    }

    private void Reposition()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var margin = Dip(MarginDip);
        var gap = Dip(GapDip);
        var y = area.Bottom - margin - Height;
        foreach (var t in Open)
        {
            if (t == this) break;
            y -= t.Height + gap; // stack newer ones above older ones
        }
        Location = new Point(area.Right - margin - Width, y);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        life.Stop();
        life.Dispose();
        Open.Remove(this);
        foreach (var t in Open) t.Reposition();
        base.OnFormClosed(e);
    }

    // Do not activate the window when shown, so it never steals keyboard focus from the active app.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008;
            const int WS_EX_TOOLWINDOW = 0x00000080; // keep it out of Alt+Tab
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}
