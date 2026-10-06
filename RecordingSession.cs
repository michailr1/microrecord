using System.Diagnostics;
using NAudio.Extras;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicroRecord;

/// <summary>
/// One recording: opens mic + system audio, mixes them on a wall-clock-paced mixer and writes a
/// 16-bit WAV next to the final file. <see cref="Finish"/> then encodes it to the chosen format.
/// </summary>
internal sealed class RecordingSession : IDisposable
{
    private readonly Action<string> log;
    private readonly AppSettings settings;
    private readonly List<LevelMeter> meters = new();
    private IAudioSource? micSource;
    private IAudioSource? systemSource;
    private RealtimeCaptureMixer? mixer;
    private WaveFileWriter? writer;
    private CancellationTokenSource? pumpCts;
    private Task? pumpTask;
    private bool started;
    private bool stopped;

    /// <summary>The final file (mp3/m4a/flac/wav) produced by <see cref="Finish"/>.</summary>
    public string OutputPath { get; }

    /// <summary>The 16-bit WAV being written while recording.</summary>
    public string TempWavPath { get; }

    /// <summary>Set when only one of mic / system audio could be opened.</summary>
    public string? Warning { get; private set; }

    public IReadOnlyList<LevelMeter> Meters => meters;

    public RecordingSession(string outputDir, AppSettings settings, Action<string> logger)
    {
        log = logger;
        this.settings = settings;
        Directory.CreateDirectory(outputDir);
        var baseName = settings.BuildFileName(DateTime.Now);
        OutputPath = UniquePath(Path.Combine(outputDir, baseName), AppSettings.Extension(settings.Format));
        TempWavPath = Path.ChangeExtension(OutputPath, ".recording.wav");
    }

    private static string UniquePath(string pathWithoutExtension, string extension)
    {
        var path = pathWithoutExtension + extension;
        for (var i = 2; File.Exists(path); i++) path = $"{pathWithoutExtension}_{i}{extension}";
        return path;
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException("Recording already started.");

        // Each side walks its own chain of capture paths (see AudioSourceFactory) and is started
        // before the mixer input exists, so a failure never leaves a dangling input behind.
        Exception? systemError = null, micError = null;
        try { systemSource = AudioSourceFactory.OpenSystem(log); } catch (Exception ex) { systemError = ex; }
        try { micSource = AudioSourceFactory.OpenMicrophone(settings, log); } catch (Exception ex) { micError = ex; }

        if (systemSource == null && micSource == null)
        {
            throw new InvalidOperationException($"Не удалось открыть ни системный звук, ни микрофон.{Environment.NewLine}{systemError?.Message}{Environment.NewLine}{micError?.Message}{Environment.NewLine}Меню в трее → Настройки → Диагностика, затем пришлите microrecord.log.");
        }
        if (systemSource == null) Warning = "Системный звук недоступен — пишется только микрофон.";
        if (micSource == null) Warning = "Микрофон недоступен — пишется только системный звук.";

        var sources = new[] { micSource, systemSource }.OfType<IAudioSource>().ToArray();
        // Split = stereo file with the mic on the left and everyone else on the right.
        var split = settings.SplitTracks && sources.Length == 2;
        var targetRate = Math.Max(44100, sources.Max(s => s.WaveFormat.SampleRate));
        mixer = new RealtimeCaptureMixer(WaveFormat.CreateIeeeFloatWaveFormat(targetRate, split ? 2 : 1));
        foreach (var source in sources)
        {
            var isMic = source == micSource;
            var volume = (isMic ? settings.MicVolumePercent : settings.SystemVolumePercent) / 100f;
            var input = mixer.AddInput(source.WaveFormat, p => split
                ? new ChannelRouter(p, toLeft: isMic, volume)
                : new VolumeSampleProvider(p) { Volume = volume });
            var meter = new LevelMeter(isMic ? "mic" : "system", source.WaveFormat, log);
            meters.Add(meter);
            source.Sink = data =>
            {
                meter.Add(data);
                input.AddSamples(data);
            };
        }

        writer = new WaveFileWriter(TempWavPath, new WaveFormat(targetRate, 16, mixer.WaveFormat.Channels));
        pumpCts = new CancellationTokenSource();
        mixer.Start();
        pumpTask = Task.Run(() => PumpAudio(pumpCts.Token));
        started = true;
        log($"recording via system=[{systemSource?.Name ?? "none"}] mic=[{micSource?.Name ?? "none"}] " +
            $"{(split ? "split L=mic R=system" : "mixed mono")} {targetRate}Hz mic={settings.MicVolumePercent}% system={settings.SystemVolumePercent}%");
    }

    private void PumpAudio(CancellationToken token)
    {
        if (mixer == null || writer == null) return;
        var buffer = new float[Math.Max(4096, mixer.WaveFormat.SampleRate * mixer.WaveFormat.Channels / 10)];
        while (!token.IsCancellationRequested)
        {
            var read = mixer.Read(buffer, 0, buffer.Length);
            if (read > 0) Write(buffer, read);
            else Thread.Sleep(5);
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 120)
        {
            var read = mixer.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            Write(buffer, read);
        }
    }

    private void Write(float[] buffer, int count)
    {
        // WaveFileWriter converts float -> short without clamping, so overs would wrap around.
        for (var i = 0; i < count; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
        writer!.WriteSamples(buffer, 0, count);
    }

    /// <summary>Stops capture and closes the temporary WAV. Fast; encoding happens in <see cref="Finish"/>.</summary>
    public void Stop()
    {
        if (!started || stopped) return;
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
    }

    /// <summary>Encodes the temporary WAV into the final format. Safe to run on a worker thread.
    /// If encoding fails the WAV is kept and its path returned.</summary>
    public string Finish()
    {
        try
        {
            return OutputEncoder.Encode(TempWavPath, OutputPath, settings.Format, settings.BitrateKbps, log);
        }
        catch (Exception ex)
        {
            log($"encode error, keeping WAV: {ex}");
            var fallback = Path.ChangeExtension(OutputPath, ".wav");
            try { File.Move(TempWavPath, fallback, overwrite: false); return fallback; }
            catch { return TempWavPath; }
        }
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

/// <summary>Downmixes a stereo input to mono and puts it on one side of a stereo output.</summary>
internal sealed class ChannelRouter : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly bool toLeft;
    private readonly float volume;

    public ChannelRouter(ISampleProvider source, bool toLeft, float volume)
    {
        if (source.WaveFormat.Channels != 2) throw new ArgumentException("ChannelRouter expects a stereo input.");
        this.source = source;
        this.toLeft = toLeft;
        this.volume = volume;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        for (var i = 0; i + 1 < read; i += 2)
        {
            var mono = (buffer[i] + buffer[i + 1]) * 0.5f * volume;
            buffer[i] = toLeft ? mono : 0f;
            buffer[i + 1] = toLeft ? 0f : mono;
        }
        return read;
    }
}
