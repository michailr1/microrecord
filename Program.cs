using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;

namespace MicroRecord;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // A second copy cannot register the hotkey and would fight over the recording.
        using var single = new Mutex(true, @"Local\MicroRecord.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("MicroRecord уже запущен (значок в трее).", "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        RemoveDownloadMark();
        ApplicationConfiguration.Initialize();
        Application.Run(new MicroRecordContext());
    }

    /// <summary>
    /// Drops the "downloaded from the Internet" mark (Zone.Identifier stream) from our own EXE, so once
    /// SmartScreen has been passed it does not ask again, also for copies of this file.
    /// </summary>
    private static void RemoveDownloadMark()
    {
        try
        {
            var mark = Environment.ProcessPath + ":Zone.Identifier";
            if (File.Exists(mark)) File.Delete(mark);
        }
        catch { } // read-only location or policy — harmless, SmartScreen just asks again next time
    }

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
}

internal static class AppIcons
{
    public static readonly Icon Idle = Load("MicroRecord.ico");
    public static readonly Icon Recording = Load("Recording.ico");

    private static Icon Load(string name)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(name);
        return stream == null ? SystemIcons.Application : new Icon(stream);
    }
}

internal sealed class MicroRecordContext : ApplicationContext
{
    private const int HotkeyId = 1;
    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkeyWindow;
    private readonly string logPath;
    private AppSettings settings;
    private RecordingSession? session;
    private SettingsForm? settingsForm;
    private EditorForm? editorForm;
    private readonly System.Windows.Forms.Timer autoStopTimer = new();
    private bool busy;

    public MicroRecordContext()
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MicroRecord");
        Directory.CreateDirectory(logDir);
        logPath = Path.Combine(logDir, "microrecord.log");
        settings = AppSettings.Load(Log);
        if (!AppSettings.FileExists())
        {
            try { Log("settings file created: " + settings.Save(Log)); }
            catch (Exception ex) { Log("settings: could not create file: " + ex.Message); }
        }
        Log($"MicroRecord v{Program.Version} started");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Начать / остановить запись", null, (_, _) => ToggleRecording());
        menu.Items.Add("Открыть папку с записями", null, (_, _) => OpenFolder());
        menu.Items.Add("Обработка записи…", null, (_, _) => ShowEditor(null));
        menu.Items.Add("Настройки…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("MicroRecord на GitHub", null, (_, _) => OpenUrl(AppSettings.GitHubUrl));
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        tray = new NotifyIcon
        {
            Icon = AppIcons.Idle,
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ToggleRecording();

        autoStopTimer.Tick += (_, _) => AutoStop();

        hotkeyWindow = new HotkeyWindow(ToggleRecording);
        RegisterHotkey(showError: true);
        SetIdleState();
        Toast.Show("MicroRecord", $"Готов. {settings.HotkeyText} — начать запись.");
    }

    private void RegisterHotkey(bool showError)
    {
        if (hotkeyWindow.Register(HotkeyId, settings.HotkeyModifiers, settings.HotkeyKey)) return;
        Log($"WARNING: failed to register {settings.HotkeyText}");
        if (showError)
        {
            MessageBox.Show($"Не удалось зарегистрировать {settings.HotkeyText} (занято другой программой). Выберите другое сочетание в настройках или используйте меню в трее.",
                "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetIdleState()
    {
        tray.Icon = AppIcons.Idle;
        tray.Text = Truncate($"MicroRecord — готов ({settings.HotkeyText})");
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63]; // NotifyIcon.Text limit

    private void ToggleRecording()
    {
        if (busy) return;
        if (session == null) StartRecording();
        else StopRecording();
    }

    private void StartRecording()
    {
        busy = true;
        try
        {
            session = new RecordingSession(settings.OutputFolder, settings.Clone(), Log);
            tray.Text = "MicroRecord — запуск…";
            session.Start();
            tray.Icon = AppIcons.Recording;
            tray.Text = Truncate($"MicroRecord — ИДЁТ ЗАПИСЬ ({settings.HotkeyText} — стоп)");
            if (settings.PlaySounds) SystemSounds.Asterisk.Play();
            StartAutoStopTimer();
            Log($"recording started: {session.TempWavPath}");
            if (session.Warning != null) Toast.Show("Неполная запись", session.Warning, ToastLevel.Warning, 5000);
            else Toast.Show("MicroRecord", "● Запись началась", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            session = null;
            SetIdleState();
            Log("start error: " + ex);
            MessageBox.Show(ex.Message, "MicroRecord — не удалось начать запись", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { busy = false; }
    }

    private async void StopRecording()
    {
        var current = session;
        if (current == null) return;
        autoStopTimer.Stop();
        busy = true;
        try
        {
            current.Stop();
            session = null;
            if (settings.PlaySounds) SystemSounds.Exclamation.Play();
            tray.Icon = AppIcons.Idle;
            tray.Text = "MicroRecord — сохранение…";
            busy = false; // a new recording may start while the previous one is being encoded
            var saved = await Task.Run(current.Finish);
            Log($"recording saved: {saved}");
            Toast.Show("Запись сохранена", Path.GetFileName(saved), ToastLevel.Success);
            if (settings.OpenFolderAfterRecording) SelectInExplorer(saved);
        }
        catch (Exception ex)
        {
            Log("stop error: " + ex);
            MessageBox.Show(ex.Message, "MicroRecord — ошибка при остановке", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            current.Dispose();
            if (session == null) SetIdleState();
            busy = false;
        }
    }

    /// <summary>Records a few seconds with the given (unsaved) settings and opens the result.</summary>
    public async Task<string> RunTestAsync(AppSettings testSettings, int seconds)
    {
        if (session != null || busy) throw new InvalidOperationException("Сначала остановите текущую запись.");
        busy = true;
        using var test = new RecordingSession(Path.Combine(Path.GetTempPath(), "MicroRecord"), testSettings, Log);
        try
        {
            test.Start();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            test.Stop();
            var path = await Task.Run(test.Finish);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            var levels = string.Join(", ", test.Meters.Select(m => $"{(m.Name == "mic" ? "микрофон" : "система")}: пик {m.TotalPeakText}"));
            return $"{levels}{(test.Warning != null ? " — " + test.Warning : "")}";
        }
        finally { busy = false; }
    }

    private void StartAutoStopTimer()
    {
        autoStopTimer.Stop();
        if (!settings.AutoStopEnabled) return;
        var minutes = Math.Clamp(settings.AutoStopMinutes, 1, 1440);
        autoStopTimer.Interval = minutes * 60_000;
        autoStopTimer.Start();
    }

    private void AutoStop()
    {
        autoStopTimer.Stop();
        if (session == null) return;
        Log($"auto-stop after {settings.AutoStopMinutes} min");
        Toast.Show("Автостоп", $"Запись остановлена автоматически после {settings.AutoStopMinutes} мин.", ToastLevel.Warning, 5000);
        StopRecording();
    }

    private void ShowEditor(string? file)
    {
        if (editorForm != null)
        {
            editorForm.Activate();
            return;
        }
        editorForm = new EditorForm(settings.Clone(), Log, SaveSplitSize, file);
        editorForm.FormClosed += (_, _) => editorForm = null;
        editorForm.Show();
    }

    private void SaveSplitSize(int mb)
    {
        if (settings.SplitSizeMb == mb) return;
        settings.SplitSizeMb = mb;
        try { settings.Save(Log); } catch (Exception ex) { Log("settings save warning: " + ex.Message); }
    }

    public void RunDiagnostics()
    {
        if (session != null)
        {
            MessageBox.Show("Сначала остановите запись.", "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Toast.Show("MicroRecord", "Идёт диагностика звука…");
        Task.Run(() => AudioDiagnostics.Run(Log)).ContinueWith(_ =>
        {
            Toast.Show("Диагностика готова", "Результат записан в microrecord.log", ToastLevel.Success);
            OpenLog();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OpenLog()
    {
        if (File.Exists(logPath)) Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
    }

    private void ShowSettings()
    {
        if (settingsForm != null)
        {
            settingsForm.Activate();
            return;
        }
        settingsForm = new SettingsForm(settings.Clone(), this);
        settingsForm.FormClosed += (_, _) => settingsForm = null;
        settingsForm.Show();
    }

    /// <summary>Called by the settings window on OK/Apply.</summary>
    public void ApplySettings(AppSettings updated, bool startWithWindows)
    {
        var hotkeyChanged = updated.HotkeyModifiers != settings.HotkeyModifiers || updated.HotkeyKey != settings.HotkeyKey;
        settings = updated;
        try { settings.Save(Log); }
        catch (Exception ex) { MessageBox.Show("Не удалось сохранить настройки: " + ex.Message, "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        try { if (settings.StartWithWindows != startWithWindows) settings.StartWithWindows = startWithWindows; }
        catch (Exception ex) { Log("autostart error: " + ex.Message); }
        if (hotkeyChanged) RegisterHotkey(showError: true);
        if (session == null) SetIdleState();
        Log($"settings saved: format={settings.Format} {settings.BitrateKbps}kbps split={settings.SplitTracks} mic={settings.MicVolumePercent}% system={settings.SystemVolumePercent}% micMode={settings.MicMode} hotkey={settings.HotkeyText}");
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(settings.OutputFolder);
        Process.Start(new ProcessStartInfo(settings.OutputFolder) { UseShellExecute = true });
    }

    private static void SelectInExplorer(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void ExitApp()
    {
        if (session != null)
        {
            var answer = MessageBox.Show("Идёт запись. Остановить и выйти?", "MicroRecord", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            // Exiting cannot wait for an async encode; keep the 16-bit WAV.
            session.Stop();
            Log($"exit during recording, WAV kept: {session.TempWavPath}");
            session.Dispose();
            session = null;
        }
        hotkeyWindow.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    private readonly object logLock = new();

    private void Log(string message)
    {
        lock (logLock)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }

    protected override void ExitThreadCore()
    {
        try { autoStopTimer.Dispose(); } catch { }
        try { session?.Dispose(); } catch { }
        try { hotkeyWindow.Dispose(); } catch { }
        try { tray.Visible = false; tray.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}

[Flags]
internal enum HotkeyModifiers : uint { Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Win = 0x0008 }

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly Action callback;
    private int registeredId;

    public HotkeyWindow(Action callback)
    {
        this.callback = callback;
        CreateHandle(new CreateParams { Caption = "MicroRecordHotkeyWindow" });
    }

    /// <summary>(Re)registers the hotkey; any previous registration is released first.</summary>
    public bool Register(int id, HotkeyModifiers modifiers, Keys key)
    {
        if (registeredId != 0) UnregisterHotKey(Handle, registeredId);
        registeredId = id;
        return RegisterHotKey(Handle, id, (uint)modifiers, (uint)key);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == registeredId) { callback(); return; }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            if (registeredId != 0) UnregisterHotKey(Handle, registeredId);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
