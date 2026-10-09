using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;

namespace MicroRecord;

/// <summary>
/// In-app updater: the same thing install.ps1 does. Checks the latest GitHub release, and if newer,
/// downloads the matching EXE (full or framework-dependent), verifies its SHA-256 against
/// SHA256SUMS.txt, swaps it in next to the running EXE and restarts. No admin rights, no SmartScreen
/// (we download it ourselves, so the file carries no "from the Internet" mark).
/// </summary>
internal static class Updater
{
    private const string Owner = "michailr1";
    private const string Repo = "microrecord";
    private const string ReleaseBase = $"https://github.com/{Owner}/{Repo}/releases/latest/download";
    private const string ApiLatest = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>Which release asset matches this build (set at build time via AssemblyMetadata "Flavor").</summary>
    private static string AssetName =>
        Flavor == "small" ? "MicroRecord-win-x64-net9.exe" : "MicroRecord-win-x64.exe";

    private static string Flavor =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "Flavor")?.Value ?? "full";

    /// <summary>Deletes the previous EXE left behind by a completed update. Call once on startup.</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            var old = BackupPath;
            if (old != null && File.Exists(old)) File.Delete(old);
        }
        catch { }
    }

    private static string? ExePath => Environment.ProcessPath;
    private static string? BackupPath => ExePath == null ? null : Path.ChangeExtension(ExePath, ".old.exe");

    /// <summary>
    /// Runs the update. <paramref name="status"/> is called on the UI thread with progress text.
    /// Returns true when an update was started (the app should then exit); false when already current.
    /// Throws on failure.
    /// </summary>
    public static async Task<bool> UpdateAsync(Action<string> status, Action<string> log)
    {
        if (ExePath == null) throw new InvalidOperationException("Не удалось определить путь к программе.");
        var dir = Path.GetDirectoryName(ExePath)!;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MicroRecord-Updater");
        http.Timeout = TimeSpan.FromMinutes(5);

        status("Проверка последней версии…");
        var latestTag = ParseTag(await http.GetStringAsync(ApiLatest));
        var latest = Normalize(latestTag);
        var current = Program.Version;
        log($"update: current {current}, latest {latestTag} ({AssetName})");
        if (!IsNewer(latest, current))
        {
            status($"У вас уже последняя версия ({current}).");
            return false;
        }

        if (!IsDirectoryWritable(dir))
        {
            throw new UnauthorizedAccessException(
                $"Нет прав на запись в папку программы ({dir}). Переустановите в пользовательскую папку командой install.ps1 или запустите как администратор.");
        }

        status($"Загрузка версии {latest}…");
        var tmp = Path.Combine(dir, "MicroRecord.update.exe");
        var bytes = await http.GetByteArrayAsync($"{ReleaseBase}/{AssetName}");
        await File.WriteAllBytesAsync(tmp, bytes);

        status("Проверка контрольной суммы…");
        var sums = await http.GetStringAsync($"{ReleaseBase}/SHA256SUMS.txt");
        var expected = ExpectedHash(sums, AssetName);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expected == null || actual != expected)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("Контрольная сумма не совпала — обновление отменено.");
        }

        status("Установка обновления…");
        var backup = BackupPath!;
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(ExePath, backup);   // a running EXE can be renamed, just not deleted in place
        File.Move(tmp, ExePath);
        log($"update: {current} -> {latest}, restarting");

        // Wait for this instance to exit (and release the single-instance mutex), then relaunch.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{ExePath}\"",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
        status($"Обновление установлено, перезапуск до {latest}…");
        return true;
    }

    private static string ParseTag(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("Нет tag_name в ответе GitHub.");
    }

    private static string Normalize(string tag) => tag.TrimStart('v', 'V').Trim();

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c)) return l > c;
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExpectedHash(string sums, string asset)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Equals(asset, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".writetest-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
