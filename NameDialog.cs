namespace MicroRecord;

/// <summary>Small prompt shown before recording: a file-name field pre-filled with the current date/time.</summary>
internal sealed class NameDialog : Form
{
    private readonly TextBox nameBox = new();
    private readonly FlowLayoutPanel root;

    private NameDialog(string defaultName)
    {
        Text = "Название записи";
        Icon = AppIcons.Recording;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;

        nameBox.Text = defaultName;
        nameBox.Width = 380;
        nameBox.Margin = new Padding(0, 0, 0, 12);

        var hint = new Label
        {
            Text = "Имя файла записи. Дата и время уже подставлены — можно оставить или дописать название.",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };

        var ok = new Button { Text = "Начать запись", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(8, 3, 0, 3) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(14) };
        root.Controls.Add(hint);
        root.Controls.Add(nameBox);
        root.Controls.Add(buttons);
        Controls.Add(root);

        // Put the caret at the end so the user types a title after the date, without clearing it.
        Shown += (_, _) => { nameBox.Focus(); nameBox.SelectionStart = nameBox.TextLength; };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var size = root.PreferredSize;
        ClientSize = new Size(size.Width, size.Height);
        CenterToScreen();
    }

    /// <summary>Shows the prompt. Returns the chosen name, or null if the user cancelled.</summary>
    public static string? Ask(string defaultName)
    {
        using var dialog = new NameDialog(defaultName);
        return dialog.ShowDialog() == DialogResult.OK ? AppSettings.Sanitize(dialog.nameBox.Text) : null;
    }
}
