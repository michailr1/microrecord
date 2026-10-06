using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Extras;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicroRecord;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
        Log("MicroRecord v0.6 started (NAudio legacy WASAPI backend)");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Start / Stop recording", null, (_, _) => ToggleRecording());
        menu.Items.Add("Open recordings folder", null, (_, _) => OpenFolder());
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

    private void Log(string message)
    {
        try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { }
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
    private WasapiCapture? micRecorder;
    private WasapiLoopbackCapture? systemRecorder;
    private RealtimeCaptureMixer? mixer;
    private WaveFileWriter? writer;
    private CancellationTokenSource? pumpCts;
    private Task? pumpTask;
    private bool started;
    private bool stopped;

    public string OutputPath { get; }

    public RecordingSession(string outputDir, Action<string> logger)
    {
        log = logger;
        OutputPath = Path.Combine(outputDir, $"meeting_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException("Recording already started.");

#pragma warning disable CS0618
        // The newer NAudio 3 WasapiRecorder path produced E_INVALIDARG on this USB headset.
        // The mature legacy classes use a different initialization path, polling sync and
        // AutoConvertPcm/SrcDefaultQuality flags, which is more tolerant of USB audio drivers.
        micRecorder = new WasapiCapture();
        systemRecorder = new WasapiLoopbackCapture();
#pragma warning restore CS0618

        log($"mic format: {micRecorder.WaveFormat}");
        log($"system format: {systemRecorder.WaveFormat}");

        var targetRate = Math.Max(44100, Math.Max(micRecorder.WaveFormat.SampleRate, systemRecorder.WaveFormat.SampleRate));
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetRate, 2);
        mixer = new RealtimeCaptureMixer(targetFormat);

        var micInput = mixer.AddInput(micRecorder.WaveFormat, p => new VolumeSampleProvider(p) { Volume = 0.5f });
        var systemInput = mixer.AddInput(systemRecorder.WaveFormat, p => new VolumeSampleProvider(p) { Volume = 0.5f });

        micRecorder.DataAvailable += (_, a) => micInput.AddSamples(a.Buffer.AsSpan(0, a.BytesRecorded));
        systemRecorder.DataAvailable += (_, a) => systemInput.AddSamples(a.Buffer.AsSpan(0, a.BytesRecorded));

        writer = new WaveFileWriter(OutputPath, mixer.WaveFormat);
        pumpCts = new CancellationTokenSource();
        mixer.Start();
        pumpTask = Task.Run(() => PumpAudio(pumpCts.Token));

        try
        {
            log("starting system loopback...");
            systemRecorder.StartRecording();
            log("system loopback started OK");

            log("starting microphone...");
            micRecorder.StartRecording();
            log("microphone started OK");

            started = true;
        }
        catch
        {
            try { systemRecorder?.StopRecording(); } catch { }
            try { micRecorder?.StopRecording(); } catch { }
            pumpCts?.Cancel();
            try { pumpTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            writer?.Dispose();
            writer = null;
            try { if (File.Exists(OutputPath) && new FileInfo(OutputPath).Length <= 64) File.Delete(OutputPath); } catch { }
            throw;
        }
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

        try { micRecorder?.StopRecording(); } catch (Exception ex) { log("mic stop warning: " + ex.Message); }
        try { systemRecorder?.StopRecording(); } catch (Exception ex) { log("system stop warning: " + ex.Message); }
        Thread.Sleep(120);

        pumpCts?.Cancel();
        try { pumpTask?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) { log("pump stop warning: " + ex.Message); }

        writer?.Dispose();
        writer = null;
        micRecorder?.Dispose();
        micRecorder = null;
        systemRecorder?.Dispose();
        systemRecorder = null;
        return OutputPath;
    }

    public void Dispose()
    {
        try { if (started && !stopped) Stop(); } catch { }
        try { pumpCts?.Cancel(); } catch { }
        try { pumpCts?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
        try { micRecorder?.Dispose(); } catch { }
        try { systemRecorder?.Dispose(); } catch { }
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
