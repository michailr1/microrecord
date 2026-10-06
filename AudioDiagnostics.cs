using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicroRecord;

/// <summary>
/// One-shot matrix of independent capture attempts, written to the log. It is meant to separate
/// "these Initialize parameters are wrong" from "every IAudioClient in this process/endpoint fails",
/// instead of guessing parameters one build at a time.
/// </summary>
internal static class AudioDiagnostics
{
    private const long Hns100Ms = 1_000_000;

    public static void Run(Action<string> log)
    {
        log("===== audio diagnostics begin =====");
        Safe(log, "environment", () => LogEnvironment(log));

        using var enumerator = new MMDeviceEnumerator();
        Safe(log, "defaults", () => LogDefaults(log, enumerator));

        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    Safe(log, "endpoint", () => ProbeEndpoint(log, device, loopback: flow == DataFlow.Render));
                }
            }
        }

        Safe(log, "activate-default", () => ProbeActivatedDefault(log, DataFlow.Render));
        Safe(log, "activate-default", () => ProbeActivatedDefault(log, DataFlow.Capture));
        ProbeSource(log, "process loopback (exclude self) 48000Hz float", () => new RecorderSource("process loopback",
            Task.Run(new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync).GetAwaiter().GetResult()));
        ProbeSource(log, "WinMM waveIn 44100Hz/16bit/mono", () => new WaveInSource("WinMM waveIn", new WaveInEvent { WaveFormat = new WaveFormat(44100, 16, 1) }));
        log("===== audio diagnostics end =====");
    }

    private static void LogEnvironment(Action<string> log)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        log($"os: {key?.GetValue("ProductName")} {key?.GetValue("DisplayVersion")} build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}");
        log($"arch: process={RuntimeInformation.ProcessArchitecture} os={RuntimeInformation.OSArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
        using var identity = WindowsIdentity.GetCurrent();
        log($"elevated={new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)} diag-thread-apartment={Thread.CurrentThread.GetApartmentState()} session={Environment.GetEnvironmentVariable("SESSIONNAME")}");
    }

    private static void LogDefaults(Action<string> log, MMDeviceEnumerator enumerator)
    {
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device))
                {
                    using (device) log($"default {flow}/{role}: {device.FriendlyName} [{device.ID}]");
                }
                else log($"default {flow}/{role}: none");
            }
        }
    }

    private static void ProbeEndpoint(Action<string> log, MMDevice device, bool loopback)
    {
        log($"--- {(loopback ? "render (loopback)" : "capture")}: {device.FriendlyName} [{device.ID}]");
        WaveFormat mix;
        using (var client = device.CreateAudioClient())
        {
            mix = client.MixFormat;
            log($"    mix: {mix} extraSize={mix.ExtraSize} bytes={Convert.ToHexString(mix.ToWaveFormatExBytes())}");
            log($"    period default={client.DefaultDevicePeriod} min={client.MinimumDevicePeriod} IAudioClient2={client.SupportsAudioClient2} IAudioClient3={client.SupportsAudioClient3}");
        }

        var baseFlags = loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
        var pcm16 = new WaveFormat(mix.SampleRate, 16, mix.Channels);
        var cases = new List<(string Name, AudioClientStreamFlags Flags, long Buffer, WaveFormat Format, Action<AudioClient>? Prepare)>
        {
            ("mix, no extra flags, 100ms", baseFlags, Hns100Ms, mix, null),
            ("mix, no extra flags, 0ms", baseFlags, 0, mix, null),
            ("mix, event callback, 0ms", baseFlags | AudioClientStreamFlags.EventCallback, 0, mix, null),
            ("mix, autoconvert+src (NAudio legacy)", baseFlags | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, Hns100Ms, mix, null),
            ("pcm16, autoconvert+src", baseFlags | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, Hns100Ms, pcm16, null),
            ("mix, category=Other", baseFlags, Hns100Ms, mix, c => c.SetClientProperties(AudioStreamCategory.Other)),
        };
        if (!loopback) cases.Add(("mix, raw mode", baseFlags, Hns100Ms, mix, c => c.SetClientProperties(AudioStreamCategory.Other, AudioClientStreamOptions.Raw)));

        foreach (var c in cases)
        {
            log($"    {c.Name}: STA={TryInitialize(device, c.Flags, c.Buffer, c.Format, c.Prepare, ApartmentState.STA)} MTA={TryInitialize(device, c.Flags, c.Buffer, c.Format, c.Prepare, ApartmentState.MTA)}");
        }
    }

    /// <summary>Creates a fresh IAudioClient on a thread of the given apartment and runs Initialize (+Start/Stop when polling).</summary>
    private static string TryInitialize(MMDevice device, AudioClientStreamFlags flags, long buffer, WaveFormat format, Action<AudioClient>? prepare, ApartmentState apartment)
    {
        var id = device.ID;
        string result = "?";
        var thread = new Thread(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var dev = enumerator.GetDevice(id);
                using var client = dev.CreateAudioClient();
                result = InitializeAndStart(client, flags, buffer, format, prepare);
            }
            catch (Exception ex) { result = $"{AudioSourceFactory.DescribeHResult(ex)} ({ex.GetType().Name} before Initialize)"; }
        });
        thread.SetApartmentState(apartment);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5))) return "timeout";
        return result;
    }

    private static string InitializeAndStart(AudioClient client, AudioClientStreamFlags flags, long buffer, WaveFormat format, Action<AudioClient>? prepare)
    {
        try { prepare?.Invoke(client); }
        catch (Exception ex) { return $"prepare {AudioSourceFactory.DescribeHResult(ex)}"; }
        try { client.Initialize(AudioClientShareMode.Shared, flags, buffer, 0, format, Guid.Empty); }
        catch (Exception ex) { return $"init {AudioSourceFactory.DescribeHResult(ex)}"; }
        if ((flags & AudioClientStreamFlags.EventCallback) != 0) return "init OK";
        try
        {
            client.Start();
            Thread.Sleep(150);
            var padding = client.CurrentPadding;
            client.Stop();
            return $"OK (padding {padding})";
        }
        catch (Exception ex) { return $"init OK, start {AudioSourceFactory.DescribeHResult(ex)}"; }
    }

    private static void ProbeActivatedDefault(Action<string> log, DataFlow flow)
    {
        var loopback = flow == DataFlow.Render;
        var name = $"ActivateAudioInterfaceAsync default {flow}{(loopback ? " (loopback)" : "")}";
        try
        {
            using var client = Task.Run(() => AudioClient.ActivateDefaultDeviceAsync(flow)).GetAwaiter().GetResult();
            var mix = client.MixFormat;
            var flags = loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
            log($"{name}: mix {mix} -> {InitializeAndStart(client, flags, Hns100Ms, mix, null)}");
        }
        catch (Exception ex) { log($"{name}: activate {AudioSourceFactory.DescribeHResult(ex)} {ex.Message}"); }
    }

    private static void ProbeSource(Action<string> log, string name, Func<IAudioSource> create)
    {
        IAudioSource? source = null;
        try
        {
            source = create();
            long bytes = 0;
            source.Sink = data => Interlocked.Add(ref bytes, data.Length);
            source.Start();
            Thread.Sleep(500);
            source.Stop();
            log($"{name}: OK, format {source.WaveFormat}, {Interlocked.Read(ref bytes)} bytes in 500ms");
        }
        catch (Exception ex) { log($"{name}: FAILED {AudioSourceFactory.DescribeHResult(ex)} {ex.GetType().Name} {ex.Message}"); }
        finally { try { source?.Dispose(); } catch { } }
    }

    private static void Safe(Action<string> log, string step, Action action)
    {
        try { action(); }
        catch (Exception ex) { log($"{step}: {AudioSourceFactory.DescribeHResult(ex)} {ex}"); }
    }
}
