using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicroRecord;

internal delegate void AudioDataHandler(ReadOnlySpan<byte> data);

/// <summary>
/// One capture stream (microphone or system audio) regardless of the Windows API behind it.
/// Sources are started before <see cref="Sink"/> is attached, so anything captured in the
/// first few milliseconds before the mixer input exists is simply dropped.
/// </summary>
internal interface IAudioSource : IDisposable
{
    string Name { get; }
    WaveFormat WaveFormat { get; }
    AudioDataHandler? Sink { get; set; }
    void Start();
    void Stop();
}

/// <summary>Wraps the classic IWaveIn implementations: WasapiCapture, WasapiLoopbackCapture, WaveInEvent.</summary>
internal sealed class WaveInSource : IAudioSource
{
    private readonly IWaveIn waveIn;

    public WaveInSource(string name, IWaveIn waveIn)
    {
        Name = name;
        this.waveIn = waveIn;
        waveIn.DataAvailable += (_, a) => Sink?.Invoke(a.Buffer.AsSpan(0, a.BytesRecorded));
    }

    public string Name { get; }
    public WaveFormat WaveFormat => waveIn.WaveFormat;
    public AudioDataHandler? Sink { get; set; }
    public void Start() => waveIn.StartRecording();
    public void Stop() => waveIn.StopRecording();
    public void Dispose() => waveIn.Dispose();
}

/// <summary>Wraps the NAudio 3 WasapiRecorder (used for ActivateAudioInterfaceAsync-based paths).</summary>
internal sealed class RecorderSource : IAudioSource
{
    private readonly WasapiRecorder recorder;

    public RecorderSource(string name, WasapiRecorder recorder)
    {
        Name = name;
        this.recorder = recorder;
        // WasapiRecorder already substitutes zeros for AUDCLNT_BUFFERFLAGS_SILENT packets.
        recorder.DataAvailable += (buffer, _, _, _) => Sink?.Invoke(buffer);
    }

    public string Name { get; }
    public WaveFormat WaveFormat => recorder.WaveFormat;
    public AudioDataHandler? Sink { get; set; }
    // Activation happened on an MTA thread; initialize there too to keep the apartment consistent.
    public void Start() => Task.Run(recorder.StartRecording).GetAwaiter().GetResult();
    public void Stop() => recorder.StopRecording();
    public void Dispose() => recorder.Dispose();
}

/// <summary>
/// Opens the microphone / system audio by trying several independent Windows capture paths in turn.
/// The first one whose IAudioClient::Initialize + Start succeeds wins; every failure is logged.
/// </summary>
internal static class AudioSourceFactory
{
    private static readonly WaveFormat ProcessLoopbackFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public static IAudioSource OpenSystem(Action<string> log) => OpenFirst("system", log,
    [
#pragma warning disable CS0618
        ("WASAPI loopback on default render endpoint", () => new WaveInSource("WASAPI loopback", new WasapiLoopbackCapture())),
#pragma warning restore CS0618
        // Captures every process except MicroRecord itself through the process-loopback virtual
        // device (Windows 10 2004+). It does not open the headset endpoint at all, so it sidesteps
        // endpoint/driver-specific Initialize failures.
        ("process loopback (all apps except MicroRecord)", () => new RecorderSource("process loopback", BuildAsync(new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
            .WithFormat(ProcessLoopbackFormat)))),
    ]);

    public static IAudioSource OpenMicrophone(AppSettings settings, Action<string> log)
    {
        var browser = ("default browser tab getUserMedia", (Func<IAudioSource>)(() =>
            new BrowserMicSource(log, settings.BrowserAutoGain, settings.BrowserNoiseSuppression)));
        if (settings.MicMode == MicMode.BrowserOnly) return OpenFirst("mic", log, [browser]);
        return OpenFirst("mic", log,
    [
#pragma warning disable CS0618
        ("WASAPI capture on default capture endpoint", () => new WaveInSource("WASAPI capture", new WasapiCapture())),
#pragma warning restore CS0618
        ("WASAPI capture via ActivateAudioInterfaceAsync (default device routing)", () => new RecorderSource("WASAPI default-routing capture",
            BuildAsync(new WasapiRecorderBuilder().WithDefaultDeviceStreamRouting()))),
        ("WinMM waveIn 44100Hz/16bit/mono", () => new WaveInSource("WinMM waveIn", new WaveIn
        {
            WaveFormat = new WaveFormat(44100, 16, 1),
            BufferMilliseconds = 50
        })),
        // Last resort: the user's own browser (trusted by endpoint security) opens the mic in a tab
        // and streams it to us over localhost. Embedded WebView2 did not help: it runs as our child process.
        browser,
    ]);
    }

    private static WasapiRecorder BuildAsync(WasapiRecorderBuilder builder) =>
        // ActivateAudioInterfaceAsync must not complete back onto the WinForms STA thread we'd be blocking.
        Task.Run(builder.BuildAsync).GetAwaiter().GetResult();

    private static IAudioSource OpenFirst(string kind, Action<string> log, (string Name, Func<IAudioSource> Create)[] attempts)
    {
        var failures = new List<string>();
        foreach (var (name, create) in attempts)
        {
            IAudioSource? source = null;
            try
            {
                log($"{kind}: trying {name}...");
                source = create();
                source.Start();
                log($"{kind}: OK via {name}, format {source.WaveFormat}");
                return source;
            }
            catch (Exception ex)
            {
                try { source?.Dispose(); } catch { }
                var reason = $"{kind}: {name} failed: {ex.GetType().Name} {DescribeHResult(ex)} {ex.Message}";
                log(reason);
                failures.Add(reason);
            }
        }
        throw new InvalidOperationException($"No working {kind} capture path:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    public static string DescribeHResult(Exception ex) => $"0x{ex.HResult:X8}";
}
