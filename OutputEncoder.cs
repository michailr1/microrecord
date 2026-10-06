using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MicroRecord;

/// <summary>
/// Turns the 16-bit WAV written during recording into the chosen format with the encoders that ship
/// with Windows (Media Foundation), so no codec libraries are bundled.
/// </summary>
internal static class OutputEncoder
{
    /// <summary>Encodes <paramref name="wavPath"/> to <paramref name="outputPath"/>; returns the final file path.</summary>
    public static string Encode(string wavPath, string outputPath, OutputFormat format, int bitrateKbps, Action<string> log)
    {
        if (format == OutputFormat.Wav)
        {
            File.Move(wavPath, outputPath, overwrite: true);
            return outputPath;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        MediaFoundationApi.Startup();
        using (var reader = new WaveFileReader(wavPath))
        {
            var bitrate = bitrateKbps * 1000;
            switch (format)
            {
                case OutputFormat.Mp3: MediaFoundationEncoder.EncodeToMp3(reader, outputPath, bitrate); break;
                case OutputFormat.M4a: MediaFoundationEncoder.EncodeToAac(reader, outputPath, bitrate); break;
                case OutputFormat.Flac: MediaFoundationEncoder.EncodeToFlac(reader, outputPath); break;
            }
        }
        log($"encoded {format} {(format == OutputFormat.Flac ? "lossless" : bitrateKbps + " kbps")} in {stopwatch.Elapsed.TotalSeconds:0.0}s: " +
            $"{new FileInfo(wavPath).Length / 1048576.0:0.0} MB -> {new FileInfo(outputPath).Length / 1048576.0:0.0} MB");
        File.Delete(wavPath);
        return outputPath;
    }
}
