namespace MicroRecord;

/// <summary>Tabbed settings window: Recording, File, General, Diagnostics, About.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings settings;
    private readonly MicroRecordContext app;

    private readonly TrackBar micVolume = Slider();
    private readonly TrackBar systemVolume = Slider();
    private readonly Label micVolumeText = new() { AutoSize = true };
    private readonly Label systemVolumeText = new() { AutoSize = true };
    private readonly CheckBox autoGain = new() { Text = "Автоусиление микрофона (браузер)", AutoSize = true };
    private readonly CheckBox noiseSuppression = new() { Text = "Шумоподавление (браузер)", AutoSize = true };
    private readonly ComboBox micMode = DropDown();
    private readonly CheckBox splitTracks = new() { Text = "Раздельные дорожки: слева микрофон, справа собеседники", AutoSize = true };
    private readonly Button testButton = new() { Text = "Тест 5 секунд", AutoSize = true };
    private readonly Label testResult = new() { AutoSize = true, MaximumSize = new Size(440, 0) };

    private readonly ComboBox format = DropDown();
    private readonly ComboBox bitrate = DropDown();
    private readonly Label sizeEstimate = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox folder = new() { Width = 300 };
    private readonly TextBox fileNameTemplate = new() { Width = 300 };
    private readonly CheckBox openFolderAfter = new() { Text = "Показать файл в проводнике после записи", AutoSize = true };

    private readonly TextBox hotkey = new() { Width = 200, ReadOnly = true, BackColor = SystemColors.Window };
    private readonly CheckBox startWithWindows = new() { Text = "Запускать вместе с Windows", AutoSize = true };
    private readonly CheckBox playSounds = new() { Text = "Звуковой сигнал при начале и окончании записи", AutoSize = true };

    private HotkeyModifiers hotkeyModifiers;
    private Keys hotkeyKey;

    private static readonly (OutputFormat Format, string Text)[] Formats =
    [
        (OutputFormat.Mp3, "MP3 — открывается везде"),
        (OutputFormat.M4a, "M4A (AAC) — лучше качество при том же размере"),
        (OutputFormat.Flac, "FLAC — без потерь, ~в 2 раза меньше WAV"),
        (OutputFormat.Wav, "WAV — без сжатия"),
    ];

    public SettingsForm(AppSettings settings, MicroRecordContext app)
    {
        this.settings = settings;
        this.app = app;

        Text = "MicroRecord — настройки";
        Icon = AppIcons.Idle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 470);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(RecordingTab());
        tabs.TabPages.Add(FileTab());
        tabs.TabPages.Add(GeneralTab());
        tabs.TabPages.Add(DiagnosticsTab());
        tabs.TabPages.Add(AboutTab());

        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true };
        var apply = new Button { Text = "Применить", AutoSize = true };
        ok.Click += (_, _) => { if (Commit()) Close(); };
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => Commit();
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        buttons.Controls.AddRange([apply, cancel, ok]);

        Controls.Add(tabs);
        Controls.Add(buttons);
        LoadValues();
    }

    private static TrackBar Slider() => new() { Minimum = 0, Maximum = 200, TickFrequency = 25, SmallChange = 5, LargeChange = 10, Width = 300 };
    private static ComboBox DropDown() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };

    private static TabPage Page(string title, params Control[] rows)
    {
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        foreach (var row in rows)
        {
            row.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(row);
        }
        var page = new TabPage(title);
        page.Controls.Add(layout);
        return page;
    }

    private static Control Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in controls)
        {
            c.Anchor = AnchorStyles.Left;
            row.Controls.Add(c);
        }
        return row;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = SystemColors.GrayText };

    private TabPage RecordingTab()
    {
        micVolume.ValueChanged += (_, _) => micVolumeText.Text = $"{micVolume.Value}%";
        systemVolume.ValueChanged += (_, _) => systemVolumeText.Text = $"{systemVolume.Value}%";
        micMode.Items.AddRange(["Автоматически (сначала Windows, затем вкладка браузера)", "Сразу через вкладку браузера"]);
        testButton.Click += async (_, _) => await RunTest();

        return Page("Запись",
            Caption("Громкость"),
            Row(new Label { Text = "Микрофон", Width = 120 }, micVolume, micVolumeText),
            Row(new Label { Text = "Системный звук", Width = 120 }, systemVolume, systemVolumeText),
            Caption("Микрофон"),
            micMode,
            Hint("Если антивирус (например, Kaspersky) не даёт программам открывать микрофон, его пишет вкладка вашего браузера. Вкладку нельзя закрывать во время записи."),
            autoGain,
            noiseSuppression,
            Caption("Дорожки"),
            splitTracks,
            Hint("Громкость себя и собеседников потом можно поправить отдельно в любом редакторе; расшифровщикам проще отличать говорящих."),
            Row(testButton, testResult));
    }

    private TabPage FileTab()
    {
        foreach (var (_, text) in Formats) format.Items.Add(text);
        format.SelectedIndexChanged += (_, _) => RefreshBitrates();
        bitrate.SelectedIndexChanged += (_, _) => RefreshEstimate();
        splitTracks.CheckedChanged += (_, _) => RefreshEstimate();
        var browse = new Button { Text = "Обзор…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = folder.Text, UseDescriptionForTitle = true, Description = "Папка для записей" };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };

        return Page("Файл",
            Caption("Формат"),
            format,
            Row(new Label { Text = "Битрейт", Width = 80 }, bitrate),
            sizeEstimate,
            Hint("Во время записи пишется временный WAV, после остановки он сжимается встроенными кодеками Windows. Если сжать не удалось, остаётся WAV."),
            Caption("Сохранение"),
            Row(folder, browse),
            Row(new Label { Text = "Имя файла", Width = 80 }, fileNameTemplate),
            Hint("{date} — дата (20261006), {time} — время (191154)."),
            openFolderAfter);
    }

    private TabPage GeneralTab()
    {
        hotkey.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin) return;
            var modifiers = (e.Control ? HotkeyModifiers.Control : 0) | (e.Alt ? HotkeyModifiers.Alt : 0) | (e.Shift ? HotkeyModifiers.Shift : 0);
            if (modifiers == 0) return; // a bare key would hijack normal typing
            hotkeyModifiers = modifiers;
            hotkeyKey = e.KeyCode;
            hotkey.Text = AppSettings.FormatHotkey(hotkeyModifiers, hotkeyKey);
        };

        return Page("Общие",
            Caption("Горячая клавиша"),
            hotkey,
            Hint("Щёлкните в поле и нажмите новое сочетание (с Ctrl, Alt или Shift)."),
            Caption("Прочее"),
            startWithWindows,
            playSounds);
    }

    private TabPage DiagnosticsTab()
    {
        var diagnose = new Button { Text = "Диагностика звука", AutoSize = true };
        var openLog = new Button { Text = "Открыть лог", AutoSize = true };
        diagnose.Click += (_, _) => app.RunDiagnostics();
        openLog.Click += (_, _) => app.OpenLog();

        return Page("Диагностика",
            Hint("Проверяет все способы захвата звука (устройства Windows, process loopback, WinMM) и записывает результат в microrecord.log. Пригодится, если запись не работает — приложите лог к issue на GitHub."),
            Row(diagnose, openLog));
    }

    private TabPage AboutTab()
    {
        var icon = new PictureBox { Image = AppIcons.Idle.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64) };
        var title = new Label { Text = $"MicroRecord {Program.Version}", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14, FontStyle.Bold) };
        var link = new LinkLabel { Text = AppSettings.GitHubUrl, AutoSize = true };
        link.LinkClicked += (_, _) => MicroRecordContext.OpenUrl(AppSettings.GitHubUrl);

        return Page("О программе",
            Row(icon, title),
            Hint("Портативный рекордер встреч для Windows: одна горячая клавиша — и микрофон вместе со звуком собеседников пишутся в один файл. Без облака, без телеметрии, без установки."),
            link);
    }

    private void LoadValues()
    {
        micVolume.Value = Math.Clamp(settings.MicVolumePercent, 0, 200);
        systemVolume.Value = Math.Clamp(settings.SystemVolumePercent, 0, 200);
        micVolumeText.Text = $"{micVolume.Value}%";
        systemVolumeText.Text = $"{systemVolume.Value}%";
        autoGain.Checked = settings.BrowserAutoGain;
        noiseSuppression.Checked = settings.BrowserNoiseSuppression;
        micMode.SelectedIndex = settings.MicMode == MicMode.BrowserOnly ? 1 : 0;
        splitTracks.Checked = settings.SplitTracks;

        format.SelectedIndex = Math.Max(0, Array.FindIndex(Formats, f => f.Format == settings.Format));
        RefreshBitrates();
        folder.Text = settings.OutputFolder;
        fileNameTemplate.Text = settings.FileNameTemplate;
        openFolderAfter.Checked = settings.OpenFolderAfterRecording;

        hotkeyModifiers = settings.HotkeyModifiers;
        hotkeyKey = settings.HotkeyKey;
        hotkey.Text = settings.HotkeyText;
        try { startWithWindows.Checked = settings.StartWithWindows; } catch { }
        playSounds.Checked = settings.PlaySounds;
    }

    private OutputFormat SelectedFormat => Formats[Math.Max(0, format.SelectedIndex)].Format;

    private void RefreshBitrates()
    {
        var rates = AppSettings.BitratesFor(SelectedFormat);
        var previous = bitrate.SelectedItem is string s && int.TryParse(s.Split(' ')[0], out var p) ? p : settings.BitrateKbps;
        bitrate.Items.Clear();
        foreach (var r in rates) bitrate.Items.Add($"{r} кбит/с");
        bitrate.Enabled = rates.Length > 0;
        if (rates.Length > 0)
        {
            var best = rates.OrderBy(r => Math.Abs(r - previous)).First();
            bitrate.SelectedIndex = Array.IndexOf(rates, best);
        }
        RefreshEstimate();
    }

    private int SelectedBitrate => bitrate.SelectedItem is string s && int.TryParse(s.Split(' ')[0], out var r) ? r : settings.BitrateKbps;

    private void RefreshEstimate()
    {
        var channels = splitTracks.Checked ? 2 : 1;
        var wavMbPerHour = 48000.0 * 2 * channels * 3600 / 1048576;
        var mb = SelectedFormat switch
        {
            OutputFormat.Mp3 or OutputFormat.M4a => SelectedBitrate * 1000.0 / 8 * 3600 / 1048576,
            OutputFormat.Flac => wavMbPerHour * 0.5,
            _ => wavMbPerHour
        };
        sizeEstimate.Text = $"≈ {mb:0} МБ за час записи{(SelectedFormat == OutputFormat.Flac ? " (зависит от содержимого)" : "")}";
    }

    private AppSettings CollectValues()
    {
        var result = settings.Clone();
        result.MicVolumePercent = micVolume.Value;
        result.SystemVolumePercent = systemVolume.Value;
        result.BrowserAutoGain = autoGain.Checked;
        result.BrowserNoiseSuppression = noiseSuppression.Checked;
        result.MicMode = micMode.SelectedIndex == 1 ? MicMode.BrowserOnly : MicMode.Auto;
        result.SplitTracks = splitTracks.Checked;
        result.Format = SelectedFormat;
        if (bitrate.Enabled) result.BitrateKbps = SelectedBitrate;
        result.OutputFolder = folder.Text.Trim();
        result.FileNameTemplate = string.IsNullOrWhiteSpace(fileNameTemplate.Text) ? "meeting_{date}_{time}" : fileNameTemplate.Text.Trim();
        result.OpenFolderAfterRecording = openFolderAfter.Checked;
        result.HotkeyModifiers = hotkeyModifiers;
        result.HotkeyKey = hotkeyKey;
        result.PlaySounds = playSounds.Checked;
        return result;
    }

    private bool Commit()
    {
        var updated = CollectValues();
        try { Directory.CreateDirectory(updated.OutputFolder); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Папка для записей недоступна: {ex.Message}", "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        app.ApplySettings(updated, startWithWindows.Checked);
        return true;
    }

    private async Task RunTest()
    {
        testButton.Enabled = false;
        testResult.Text = "Запись 5 секунд… скажите что-нибудь";
        try { testResult.Text = await app.RunTestAsync(CollectValues(), 5); }
        catch (Exception ex) { testResult.Text = "Ошибка: " + ex.Message; }
        finally { testButton.Enabled = true; }
    }
}
