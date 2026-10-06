using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MicroRecord;

internal enum OutputFormat { Mp3, M4a, Flac, Wav }

internal enum MicMode
{
    /// <summary>Try WASAPI / WinMM first, then fall back to a browser tab.</summary>
    Auto,
    /// <summary>Go straight to the browser tab (for machines where in-process capture is blocked).</summary>
    BrowserOnly
}

/// <summary>User settings, stored as settings.json next to the EXE (portable) or in %APPDATA% if that folder is read-only.</summary>
internal sealed class AppSettings
{
    public const string GitHubUrl = "https://github.com/michailr1/microrecord";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Recording
    public int MicVolumePercent { get; set; } = 70;
    public int SystemVolumePercent { get; set; } = 70;
    public bool BrowserAutoGain { get; set; } = true;
    public bool BrowserNoiseSuppression { get; set; } = true;
    public MicMode MicMode { get; set; } = MicMode.Auto;
    public bool SplitTracks { get; set; } = true;

    // File
    public OutputFormat Format { get; set; } = OutputFormat.Mp3;
    public int BitrateKbps { get; set; } = 128;
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MicroRecord");
    public string FileNameTemplate { get; set; } = "meeting_{date}_{time}";
    public bool OpenFolderAfterRecording { get; set; }

    // General
    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public Keys HotkeyKey { get; set; } = Keys.R;
    public bool PlaySounds { get; set; } = true;

    [JsonIgnore]
    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("MicroRecord") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("MicroRecord", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("MicroRecord", throwOnMissingValue: false);
        }
    }

    [JsonIgnore]
    public string HotkeyText => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public static string FormatHotkey(HotkeyModifiers modifiers, Keys key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Bitrates the Windows encoders accept for a format (AAC only takes these four).</summary>
    public static int[] BitratesFor(OutputFormat format) => format switch
    {
        OutputFormat.Mp3 => [64, 96, 128, 160, 192, 256, 320],
        OutputFormat.M4a => [96, 128, 160, 192],
        _ => []
    };

    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Mp3 => ".mp3",
        OutputFormat.M4a => ".m4a",
        OutputFormat.Flac => ".flac",
        _ => ".wav"
    };

    public string BuildFileName(DateTime now) =>
        string.Join("_", FileNameTemplate
            .Replace("{date}", now.ToString("yyyyMMdd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string PortablePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static string AppDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicroRecord", "settings.json");

    public static AppSettings Load(Action<string> log)
    {
        foreach (var path in new[] { PortablePath, AppDataPath })
        {
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex) { log($"settings: could not read {path}: {ex.Message}"); }
        }
        return new AppSettings();
    }

    public void Save(Action<string> log)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        try
        {
            File.WriteAllText(PortablePath, json);
            return;
        }
        catch (Exception ex) { log($"settings: {PortablePath} not writable ({ex.Message}), using %APPDATA%"); }
        Directory.CreateDirectory(Path.GetDirectoryName(AppDataPath)!);
        File.WriteAllText(AppDataPath, json);
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}
