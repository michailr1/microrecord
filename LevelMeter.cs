using NAudio.Wave;

namespace MicroRecord;

/// <summary>Counts bytes and tracks peak level of one capture stream, logging a line every few seconds.</summary>
internal sealed class LevelMeter
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly string name;
    private readonly WaveFormat format;
    private readonly Action<string> log;
    private readonly object gate = new();
    private DateTime windowStart = DateTime.UtcNow;
    private long windowBytes, totalBytes;
    private float windowPeak, totalPeak;

    public LevelMeter(string name, WaveFormat format, Action<string> log)
    {
        this.name = name;
        this.format = format;
        this.log = log;
    }

    public void Add(ReadOnlySpan<byte> data)
    {
        var peak = Peak(data);
        lock (gate)
        {
            windowBytes += data.Length;
            totalBytes += data.Length;
            windowPeak = Math.Max(windowPeak, peak);
            totalPeak = Math.Max(totalPeak, peak);
            if (DateTime.UtcNow - windowStart < Interval) return;
            log($"level {name}: {windowBytes} bytes, peak {Db(windowPeak)} in last {Interval.TotalSeconds:0}s");
            windowStart = DateTime.UtcNow;
            windowBytes = 0;
            windowPeak = 0;
        }
    }

    public string Name => name;

    /// <summary>Peak of everything seen so far, e.g. "-12.3 dBFS".</summary>
    public string TotalPeakText { get { lock (gate) return Db(totalPeak); } }

    public long TotalBytes { get { lock (gate) return totalBytes; } }

    public void LogTotal()
    {
        lock (gate) log($"level {name} total: {totalBytes} bytes ({totalBytes / Math.Max(1, format.AverageBytesPerSecond):0.0}s of audio), peak {Db(totalPeak)}");
    }

    private float Peak(ReadOnlySpan<byte> data)
    {
        float peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32))
        {
            foreach (var s in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data)) peak = Math.Max(peak, Math.Abs(s));
        }
        else if (format.BitsPerSample == 16)
        {
            foreach (var s in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data)) peak = Math.Max(peak, Math.Abs(s / 32768f));
        }
        return peak;
    }

    private static string Db(float peak) => peak <= 0 ? "-inf dBFS (digital silence)" : $"{20 * Math.Log10(peak):0.0} dBFS";
}
