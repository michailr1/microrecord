namespace MicroRecord;

/// <summary>Window for trimming a finished recording to a time range and splitting it into size-limited parts.</summary>
internal sealed class EditorForm : Form
{
    private readonly AppSettings settings;
    private readonly Action<string> log;

    private readonly TextBox filePath = new() { Width = 320, ReadOnly = true };
    private readonly Label duration = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly MaskedTextBox trimStart = Time();
    private readonly MaskedTextBox trimEnd = Time();
    private readonly Button trimButton = new() { Text = "Сохранить обрезанное", AutoSize = true, Enabled = false };
    private readonly NumericUpDown splitSize = new() { Minimum = 1, Maximum = 2000, Width = 80 };
    private readonly Button splitButton = new() { Text = "Разбить на части", AutoSize = true, Enabled = false };
    private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(440, 0) };

    private string? path;
    private TimeSpan total;

    private readonly Action<int> onSplitSizeChanged;

    public EditorForm(AppSettings settings, Action<string> log, Action<int> onSplitSizeChanged, string? initialFile = null)
    {
        this.settings = settings;
        this.log = log;
        this.onSplitSizeChanged = onSplitSizeChanged;

        Text = "MicroRecord — обработка записи";
        Icon = AppIcons.Idle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 430);

        splitSize.Value = Math.Clamp(settings.SplitSizeMb, 1, 2000);

        var browse = new Button { Text = "Выбрать файл…", AutoSize = true };
        browse.Click += (_, _) => ChooseFile();
        trimButton.Click += async (_, _) => await RunTrim();
        splitButton.Click += async (_, _) => await RunSplit();

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        void Add(Control c, int bottom = 8) { c.Margin = new Padding(0, 0, 0, bottom); layout.Controls.Add(c); }

        Add(Caption("Файл записи"));
        Add(Row(filePath, browse));
        Add(duration, 16);

        Add(Caption("Обрезать по времени"));
        Add(Hint("Формат чч:мм:сс. Сохраняется копия с суффиксом _trim, исходный файл не меняется."));
        Add(Row(new Label { Text = "С", Width = 20, TextAlign = ContentAlignment.MiddleLeft }, trimStart,
                new Label { Text = "  по", Width = 36, TextAlign = ContentAlignment.MiddleLeft }, trimEnd));
        Add(trimButton, 16);

        Add(Caption("Разбить на части по размеру"));
        Add(Hint("Для загрузки на транскрибацию (у OpenAI Whisper лимит 25 МБ). Части режутся по времени, размер — приблизительный."));
        Add(Row(new Label { Text = "Размер части, МБ", Width = 120, TextAlign = ContentAlignment.MiddleLeft }, splitSize));
        Add(splitButton, 16);

        Add(result);

        Controls.Add(layout);
        if (initialFile != null && File.Exists(initialFile)) Load(initialFile);
    }

    private static MaskedTextBox Time() => new() { Mask = "00:00:00", Width = 80, ValidatingType = typeof(DateTime) };
    private static Label Caption(string t) => new() { Text = t, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private static Label Hint(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(460, 0), ForeColor = SystemColors.GrayText };

    private static Control Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in controls) { c.Anchor = AnchorStyles.Left; row.Controls.Add(c); }
        return row;
    }

    private void ChooseFile()
    {
        using var dialog = new OpenFileDialog
        {
            InitialDirectory = Directory.Exists(settings.OutputFolder) ? settings.OutputFolder : "",
            Filter = "Аудиозаписи (*.mp3;*.m4a;*.wav;*.flac)|*.mp3;*.m4a;*.wav;*.flac|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) Load(dialog.FileName);
    }

    private void Load(string file)
    {
        try
        {
            total = RecordingEditor.GetDuration(file);
            path = file;
            filePath.Text = file;
            var mb = new FileInfo(file).Length / 1048576.0;
            duration.Text = $"Длительность {total:hh\\:mm\\:ss}, размер {mb:0.0} МБ";
            trimStart.Text = "000000";
            trimEnd.Text = total.ToString("hhmmss");
            trimButton.Enabled = splitButton.Enabled = true;
            result.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть файл: " + ex.Message, "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool TryTime(MaskedTextBox box, out TimeSpan value)
    {
        value = default;
        var parts = box.Text.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s) && m < 60 && s < 60)
        {
            value = new TimeSpan(h, m, s);
            return true;
        }
        return false;
    }

    private async Task RunTrim()
    {
        if (path == null) return;
        if (!TryTime(trimStart, out var start) || !TryTime(trimEnd, out var end))
        {
            result.Text = "Проверьте поля времени (чч:мм:сс).";
            return;
        }
        if (end > total) end = total;
        if (end <= start) { result.Text = "Конец должен быть позже начала."; return; }

        var output = Suffixed(path, "trim");
        await Run(trimButton, $"Обрезаю {start:hh\\:mm\\:ss}..{end:hh\\:mm\\:ss}…", () =>
        {
            RecordingEditor.Trim(path, output, start, end, settings.BitrateKbps, log);
            return $"Сохранено: {Path.GetFileName(output)} ({new FileInfo(output).Length / 1048576.0:0.0} МБ)";
        }, output);
    }

    private async Task RunSplit()
    {
        if (path == null) return;
        onSplitSizeChanged((int)splitSize.Value); // persist the chosen size for next time
        var targetBytes = (long)splitSize.Value * 1048576;
        var input = path;
        await Run(splitButton, $"Делю на части по ~{splitSize.Value} МБ…", () =>
        {
            var parts = RecordingEditor.SplitBySize(input, targetBytes, settings.BitrateKbps, log);
            return $"Готово: {parts.Count} частей рядом с исходным файлом.";
        }, path);
    }

    private async Task Run(Button button, string progress, Func<string> work, string revealPath)
    {
        trimButton.Enabled = splitButton.Enabled = false;
        result.ForeColor = SystemColors.ControlText;
        result.Text = progress;
        try
        {
            result.Text = await Task.Run(work);
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{revealPath}\""); } catch { }
        }
        catch (Exception ex)
        {
            result.ForeColor = Color.Firebrick;
            result.Text = "Ошибка: " + ex.Message;
            log("editor error: " + ex);
        }
        finally { trimButton.Enabled = splitButton.Enabled = path != null; }
    }

    private static string Suffixed(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var result = Path.Combine(dir, $"{name}_{suffix}{ext}");
        for (var i = 2; File.Exists(result); i++) result = Path.Combine(dir, $"{name}_{suffix}{i}{ext}");
        return result;
    }
}
