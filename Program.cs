using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Extras;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicroRecord;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // A second copy cannot register Ctrl+Alt+R and would fight over the recording.
        using var single = new Mutex(true, @"Local\MicroRecord.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("MicroRecord is already running (see the tray icon).", "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MicroRecordContext());
    }
}

internal sealed class MicroRecordContext : ApplicationContext
{
    private const int HotkeyId = 1;
    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkeyWindow;
    private readonly string outputDir;
    private readonly string logPath;
    private RecordingSession? session;
    private bool stopping;

    public MicroRecordContext()
    {
        outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MicroRecord");
        Directory.CreateDirectory(outputDir);
        logPath = Path.Combine(outputDir, "microrecord.log");
        Log("MicroRecord v0.9.2 started (WASAPI with process-loopback / WinMM fallbacks)");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Start / Stop recording", null, (_, _) => ToggleRecording());
        menu.Items.Add("Open recordings folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Diagnose audio (writes log)", null, (_, _) => RunDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MicroRecord — ready (Ctrl+Alt+R)",
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ToggleRecording();
        tray.ShowBalloonTip(1500, "MicroRecord", "Ready. Ctrl+Alt+R starts recording.", ToolTipIcon.Info);

        hotkeyWindow = new HotkeyWindow(ToggleRecording);
        if (!hotkeyWindow.Register(HotkeyId, HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.R))
        {
            Log("WARNING: failed to register Ctrl+Alt+R");
            MessageBox.Show("Could not register Ctrl+Alt+R. Use the tray menu instead.", "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleRecording()
    {
        if (stopping) return;
        if (session == null) StartRecording();
        else StopRecording();
    }

    private void StartRecording()
    {
        try
        {
            session = new RecordingSession(outputDir, Log);
            session.Start();
            tray.Icon = SystemIcons.Error;
            tray.Text = "MicroRecord — RECORDING (Ctrl+Alt+R to stop)";
            tray.ShowBalloonTip(1200, "MicroRecord", "Recording started", ToolTipIcon.Info);
            Log($"recording started: {session.OutputPath}");
            if (session.Warning != null)
            {
                tray.ShowBalloonTip(4000, "MicroRecord — partial recording", session.Warning, ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            session?.Dispose();
            session = null;
            tray.Icon = SystemIcons.Application;
            tray.Text = "MicroRecord — error";
            Log("start error: " + ex);
            MessageBox.Show(ex.ToString(), "MicroRecord — recording failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopRecording()
    {
        if (session == null) return;
        stopping = true;
        try
        {
            tray.Text = "MicroRecord — stopping...";
            var completed = session.Stop();
            Log($"recording saved: {completed}");
            tray.ShowBalloonTip(1600, "MicroRecord", $"Saved: {Path.GetFileName(completed)}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log("stop error: " + ex);
            MessageBox.Show(ex.ToString(), "MicroRecord — stop failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            session.Dispose();
            session = null;
            stopping = false;
            tray.Icon = SystemIcons.Application;
            tray.Text = "MicroRecord — ready (Ctrl+Alt+R)";
        }
    }

    private void RunDiagnostics()
    {
        if (session != null)
        {
            MessageBox.Show("Stop the recording before running diagnostics.", "MicroRecord", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        tray.ShowBalloonTip(1500, "MicroRecord", "Running audio diagnostics...", ToolTipIcon.Info);
        Task.Run(() => AudioDiagnostics.Run(Log)).ContinueWith(_ =>
        {
            tray.ShowBalloonTip(2500, "MicroRecord", "Diagnostics written to microrecord.log", ToolTipIcon.Info);
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OpenFolder() => Process.Start(new ProcessStartInfo(outputDir) { UseShellExecute = true });

    private void ExitApp()
    {
        if (session != null)
        {
            var answer = MessageBox.Show("Recording is active. Stop and exit?", "MicroRecord", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            StopRecording();
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
        try { session?.Dispose(); } catch { }
        try { hotkeyWindow.Dispose(); } catch { }
        try { tray.Visible = false; tray.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}

internal sealed class RecordingSession : IDisposable
{
    private readonly Action<string> log;
    private IAudioSource? micSource;
    private IAudioSource? systemSource;
    private RealtimeCaptureMixer? mixer;
    private readonly List<LevelMeter> meters = new();
    private WaveFileWriter? writer;
    private CancellationTokenSource? pumpCts;
    private Task? pumpTask;
    private bool started;
    private bool stopped;

    public string OutputPath { get; }

    /// <summary>Set when only one of mic / system audio could be opened.</summary>
    public string? Warning { get; private set; }

    public RecordingSession(string outputDir, Action<string> logger)
    {
        log = logger;
        OutputPath = Path.Combine(outputDir, $"meeting_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException("Recording already started.");

        // Each side walks its own chain of capture paths (see AudioSourceFactory) and is started
        // before the mixer input exists, so a failure never leaves a dangling input behind.
        Exception? systemError = null, micError = null;
        try { systemSource = AudioSourceFactory.OpenSystem(log); } catch (Exception ex) { systemError = ex; }
        try { micSource = AudioSourceFactory.OpenMicrophone(log); } catch (Exception ex) { micError = ex; }

        if (systemSource == null && micSource == null)
        {
            throw new InvalidOperationException($"Neither system audio nor microphone could be opened.{Environment.NewLine}{systemError?.Message}{Environment.NewLine}{micError?.Message}{Environment.NewLine}Run tray menu → Diagnose audio and send microrecord.log.");
        }
        if (systemSource == null) Warning = "System audio unavailable — recording microphone only.";
        if (micSource == null) Warning = "Microphone unavailable — recording system audio only.";

        var sources = new[] { systemSource, micSource }.OfType<IAudioSource>().ToArray();
        var targetRate = Math.Max(44100, sources.Max(s => s.WaveFormat.SampleRate));
        mixer = new RealtimeCaptureMixer(WaveFormat.CreateIeeeFloatWaveFormat(targetRate, 2));
        foreach (var source in sources)
        {
            // Voice stays at full level; system audio is pulled down a bit so speech is not buried.
            var volume = source == micSource ? 1f : sources.Length > 1 ? 0.7f : 1f;
            var input = mixer.AddInput(source.WaveFormat, p => new VolumeSampleProvider(p) { Volume = volume });
            var meter = new LevelMeter(source == micSource ? "mic" : "system", source.WaveFormat, log);
            meters.Add(meter);
            source.Sink = data =>
            {
                meter.Add(data);
                input.AddSamples(data);
            };
        }

        writer = new WaveFileWriter(OutputPath, mixer.WaveFormat);
        pumpCts = new CancellationTokenSource();
        mixer.Start();
        pumpTask = Task.Run(() => PumpAudio(pumpCts.Token));
        started = true;
        log($"recording via system=[{systemSource?.Name ?? "none"}] mic=[{micSource?.Name ?? "none"}]");
    }

    private void PumpAudio(CancellationToken token)
    {
        if (mixer == null || writer == null) return;
        var buffer = new float[Math.Max(4096, mixer.WaveFormat.SampleRate * mixer.WaveFormat.Channels / 10)];
        while (!token.IsCancellationRequested)
        {
            var read = mixer.Read(buffer, 0, buffer.Length);
            if (read > 0) writer.WriteSamples(buffer, 0, read);
            else Thread.Sleep(5);
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 120)
        {
            var read = mixer.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            writer.WriteSamples(buffer, 0, read);
        }
    }

    public string Stop()
    {
        if (!started || stopped) return OutputPath;
        stopped = true;

        foreach (var meter in meters) meter.LogTotal();
        try { micSource?.Stop(); } catch (Exception ex) { log("mic stop warning: " + ex.Message); }
        try { systemSource?.Stop(); } catch (Exception ex) { log("system stop warning: " + ex.Message); }
        Thread.Sleep(120);

        pumpCts?.Cancel();
        try { pumpTask?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) { log("pump stop warning: " + ex.Message); }

        writer?.Dispose();
        writer = null;
        micSource?.Dispose();
        micSource = null;
        systemSource?.Dispose();
        systemSource = null;
        return OutputPath;
    }

    public void Dispose()
    {
        try { if (started && !stopped) Stop(); } catch { }
        try { pumpCts?.Cancel(); } catch { }
        try { pumpCts?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
        try { micSource?.Dispose(); } catch { }
        try { systemSource?.Dispose(); } catch { }
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

    public bool Register(int id, HotkeyModifiers modifiers, Keys key)
    {
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
