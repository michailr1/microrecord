namespace MicroRecord;

/// <summary>Small prompt shown before recording: a file-name field pre-filled with the current date/time.</summary>
internal sealed class NameDialog : Form
{
    private readonly TextBox nameBox = new() { Dock = DockStyle.Top };

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
        ClientSize = new Size(420, 120);

        nameBox.Text = defaultName;

        var hint = new Label { Text = "Имя файла записи. Дата и время уже подставлены — можно оставить или дописать название.", Dock = DockStyle.Top, AutoSize = false, Height = 40, ForeColor = SystemColors.GrayText };

        var ok = new Button { Text = "Начать запись", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        pad.Controls.Add(nameBox);
        pad.Controls.Add(hint);

        Controls.Add(pad);
        Controls.Add(buttons);

        // Put the caret at the end so the user types a title after the date, without clearing it.
        Shown += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectionStart = nameBox.TextLength;
        };
    }

    /// <summary>Shows the prompt. Returns the chosen name, or null if the user cancelled.</summary>
    public static string? Ask(string defaultName)
    {
        using var dialog = new NameDialog(defaultName);
        return dialog.ShowDialog() == DialogResult.OK ? AppSettings.Sanitize(dialog.nameBox.Text) : null;
    }
}
