using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicroRecord;

/// <summary>
/// Post-processing on a finished recording: read its real duration, trim it to a time range, or split
/// it into parts of a target size — all through the Windows (Media Foundation) codecs, re-encoding into
/// the same format as the source so no extra codec libraries are needed.
/// </summary>
internal static class RecordingEditor
{
    public static readonly string[] SupportedExtensions = [".mp3", ".m4a", ".wav", ".flac"];

    public static TimeSpan GetDuration(string path)
    {
        using var reader = new MediaFoundationReader(path);
        return reader.TotalTime;
    }

    /// <summary>Writes the [start, end) range of <paramref name="input"/> to <paramref name="output"/>.</summary>
    public static void Trim(string input, string output, TimeSpan start, TimeSpan end, int bitrateKbps, Action<string> log)
    {
        if (end <= start) throw new ArgumentException("Конец должен быть позже начала.");
        MediaFoundationApi.Startup();
        using var reader = new MediaFoundationReader(input);
        var take = (end > reader.TotalTime ? reader.TotalTime : end) - start;
        var ranged = new OffsetSampleProvider(reader.ToSampleProvider()) { SkipOver = start, Take = take };
        EncodeProvider(ranged, output, FormatOf(input), bitrateKbps, log);
        log($"trim {start:hh\\:mm\\:ss}..{end:hh\\:mm\\:ss} -> {Path.GetFileName(output)} ({Mb(output):0.0} MB)");
    }

    /// <summary>
    /// Splits <paramref name="input"/> into consecutive parts of about <paramref name="targetBytes"/> each,
    /// named "<name>_partNN.<ext>" next to it. Returns the written files. Parts are cut at exact time
    /// boundaries computed from the output bitrate, so sizes are approximate.
    /// </summary>
    public static List<string> SplitBySize(string input, long targetBytes, int bitrateKbps, Action<string> log)
    {
        MediaFoundationApi.Startup();
        using var reader = new MediaFoundationReader(input);
        var format = FormatOf(input);
        var total = reader.TotalTime;

        // bytes/second the parts are expected to weigh, to turn the size target into a duration per part.
        double bytesPerSecond = format is OutputFormat.Mp3 or OutputFormat.M4a
            ? bitrateKbps * 1000 / 8.0
            : reader.WaveFormat.AverageBytesPerSecond * (format == OutputFormat.Flac ? 0.6 : 1.0);
        var perPart = TimeSpan.FromSeconds(Math.Max(5, targetBytes / bytesPerSecond));

        var count = (int)Math.Ceiling(total.TotalSeconds / perPart.TotalSeconds);
        if (count <= 1) throw new InvalidOperationException($"Файл и так меньше цели ({Mb(input):0.0} МБ) — делить не нужно.");

        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var ext = Path.GetExtension(input);
        var parts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds(perPart.TotalSeconds * i);
            var take = start + perPart > total ? total - start : perPart;
            if (take <= TimeSpan.Zero) break;
            var part = Path.Combine(dir, $"{name}_part{i + 1:00}{ext}");
            // A fresh reader per part keeps seeking reliable across formats.
            using (var partReader = new MediaFoundationReader(input))
            {
                var ranged = new OffsetSampleProvider(partReader.ToSampleProvider()) { SkipOver = start, Take = take };
                EncodeProvider(ranged, part, format, bitrateKbps, log);
            }
            parts.Add(part);
            log($"part {i + 1}/{count}: {start:hh\\:mm\\:ss} +{take:hh\\:mm\\:ss} -> {Path.GetFileName(part)} ({Mb(part):0.0} MB)");
        }
        return parts;
    }

    private static void EncodeProvider(ISampleProvider source, string output, OutputFormat format, int bitrateKbps, Action<string> log)
    {
        var bitrate = bitrateKbps * 1000;
        switch (format)
        {
            case OutputFormat.Mp3:
                MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(source), output, bitrate);
                break;
            case OutputFormat.M4a:
                MediaFoundationEncoder.EncodeToAac(new SampleToWaveProvider16(source), output, bitrate);
                break;
            case OutputFormat.Flac:
                MediaFoundationEncoder.EncodeToFlac(new SampleToWaveProvider16(source), output);
                break;
            default:
                WaveFileWriter.CreateWaveFile16(output, source);
                break;
        }
    }

    private static OutputFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => OutputFormat.Mp3,
        ".m4a" => OutputFormat.M4a,
        ".flac" => OutputFormat.Flac,
        _ => OutputFormat.Wav
    };

    private static double Mb(string path) => new FileInfo(path).Length / 1048576.0;
}
