using System.Runtime.InteropServices;

namespace MicroRecord;

internal enum ToastLevel { Info, Success, Warning, Error }

/// <summary>
/// A small in-app notification shown instantly in the bottom-right corner, instead of a Windows
/// balloon/toast (those can arrive several seconds late or be silenced by Focus Assist). It never
/// steals focus, auto-dismisses, closes on click, and stacks upward when several are shown.
/// </summary>
internal sealed class Toast : Form
{
    private const int Margin = 12;
    private const int Gap = 8;
    private static readonly List<Toast> Open = new();

    private readonly System.Windows.Forms.Timer life = new();

    private Toast(string title, string message, ToastLevel level, int durationMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(32, 34, 40);
        var accent = level switch
        {
            ToastLevel.Success => Color.FromArgb(46, 160, 67),
            ToastLevel.Warning => Color.FromArgb(210, 153, 34),
            ToastLevel.Error => Color.FromArgb(218, 54, 51),
            _ => Color.FromArgb(47, 129, 247)
        };

        var stripe = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accent };
        var titleLabel = new Label
        {
            Text = title,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.White,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f, FontStyle.Bold),
            Padding = new Padding(12, 8, 12, 0)
        };
        var messageLabel = new Label
        {
            Text = message,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(214, 218, 224),
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
            Padding = new Padding(12, 2, 12, 10)
        };

        Width = 340;
        Height = Math.Max(64, MeasureHeight(title, message));
        Controls.Add(messageLabel);
        Controls.Add(titleLabel);
        Controls.Add(stripe);

        foreach (Control c in new Control[] { this, stripe, titleLabel, messageLabel })
            c.Click += (_, _) => Close();

        life.Interval = Math.Max(1000, durationMs);
        life.Tick += (_, _) => Close();
    }

    private static int MeasureHeight(string title, string message)
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        var font = SystemFonts.MessageBoxFont!;
        var lines = g.MeasureString(message, font, 316).Height;
        return 30 + (int)Math.Ceiling(lines) + 12;
    }

    /// <summary>Shows a toast. Must be called on the UI thread.</summary>
    public static void Show(string title, string message, ToastLevel level = ToastLevel.Info, int durationMs = 3000)
    {
        var toast = new Toast(title, message, level, durationMs);
        Open.Add(toast);
        toast.Reposition();
        toast.Show();
        toast.life.Start();
    }

    private void Reposition()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var y = area.Bottom - Margin - Height;
        foreach (var t in Open)
        {
            if (t == this) break;
            y -= t.Height + Gap; // stack newer ones above older ones
        }
        Location = new Point(area.Right - Margin - Width, y);
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
