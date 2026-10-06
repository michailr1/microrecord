using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using NAudio.Wave;

namespace MicroRecord;

/// <summary>
/// Captures the microphone through an invisible WebView2 (Edge) page using getUserMedia.
/// The audio device is then opened by Microsoft's signed msedgewebview2.exe audio service process,
/// exactly like a browser tab does, instead of by MicroRecord.exe itself. This is the fallback for
/// machines where endpoint security (e.g. Kaspersky audio input control) rejects every
/// IAudioClient::Initialize coming from an untrusted executable while browsers keep working.
/// </summary>
internal sealed class WebViewMicSource : IAudioSource
{
    private const string HostName = "microrecord.local";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly Action<string> log;
    private readonly TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? uiThread;
    private Form? host;
    private CoreWebView2Controller? controller;
    private WaveFormat? waveFormat;

    public WebViewMicSource(Action<string> log) => this.log = log;

    public string Name => "WebView2 getUserMedia";
    public WaveFormat WaveFormat => waveFormat ?? throw new InvalidOperationException("Not started.");
    public AudioDataHandler? Sink { get; set; }

    public void Start()
    {
        // WebView2 needs an STA thread with its own message loop; the WinForms UI thread is blocked
        // inside Start(), so the page lives on a dedicated thread.
        uiThread = new Thread(RunHost) { IsBackground = true, Name = "MicroRecord WebView2 mic" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        if (!started.Task.Wait(StartTimeout)) throw new TimeoutException("WebView2 microphone did not start within " + StartTimeout.TotalSeconds + "s.");
        var sampleRate = started.Task.GetAwaiter().GetResult();
        waveFormat = new WaveFormat(sampleRate, 16, 1);
    }

    private void RunHost()
    {
        try
        {
            host = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1) };
            _ = host.Handle; // create the HWND without ever showing the window
            host.BeginInvoke(new Action(async () =>
            {
                try { await InitializeAsync(); }
                catch (Exception ex) { started.TrySetException(ex); }
            }));
            Application.Run();
        }
        catch (Exception ex) { started.TrySetException(ex); }
        finally
        {
            try { controller?.Close(); } catch { }
            try { host?.Dispose(); } catch { }
        }
    }

    private async Task InitializeAsync()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroRecord");
        var webDir = Path.Combine(baseDir, "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "mic.html"), PageHtml);

        var options = new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(baseDir, "WebView2"), options);
        log($"mic: WebView2 runtime {environment.BrowserVersionString}");

        controller = await environment.CreateCoreWebView2ControllerAsync(host!.Handle);
        controller.IsVisible = true; // the host window itself stays hidden
        var web = controller.CoreWebView2;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                e.State = CoreWebView2PermissionState.Allow;
                e.SavesInProfile = true;
            }
        };
        web.WebMessageReceived += OnWebMessage;
        // https virtual host => secure context, which getUserMedia requires.
        web.SetVirtualHostNameToFolderMapping(HostName, webDir, CoreWebView2HostResourceAccessKind.Allow);
        web.Navigate($"https://{HostName}/mic.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "data":
                    Sink?.Invoke(Convert.FromBase64String(root.GetProperty("pcm").GetString()!));
                    break;
                case "started":
                    log($"mic: WebView2 capture from '{root.GetProperty("label").GetString()}' at {root.GetProperty("sampleRate").GetInt32()}Hz");
                    started.TrySetResult(root.GetProperty("sampleRate").GetInt32());
                    break;
                case "error":
                    var message = root.GetProperty("message").GetString();
                    log("mic: WebView2 page error: " + message);
                    started.TrySetException(new InvalidOperationException("WebView2 getUserMedia failed: " + message));
                    break;
            }
        }
        catch (Exception ex) { log("mic: WebView2 message error: " + ex.Message); }
    }

    public void Stop() => Shutdown();

    public void Dispose() => Shutdown();

    private void Shutdown()
    {
        var form = host;
        if (form == null || uiThread == null) return;
        host = null;
        try
        {
            form.BeginInvoke(new Action(() =>
            {
                try { controller?.CoreWebView2?.ExecuteScriptAsync("stopCapture()"); } catch { }
                Application.ExitThread();
            }));
        }
        catch { }
        uiThread.Join(TimeSpan.FromSeconds(3));
    }

    // Plain getUserMedia -> AudioWorklet -> 16-bit mono PCM, batched ~100 ms, posted to the host as base64.
    // Browser voice processing is disabled so the recording matches the raw microphone.
    private const string PageHtml = """
<!doctype html>
<meta charset="utf-8">
<script>
const post = m => window.chrome.webview.postMessage(m);
let ctx, stream;
const worklet = `
class Pcm extends AudioWorkletProcessor {
  constructor() { super(); this.buf = new Int16Array(Math.round(sampleRate / 10)); this.n = 0; }
  process(inputs) {
    const ch = inputs[0] && inputs[0][0];
    if (ch) for (let i = 0; i < ch.length; i++) {
      const s = Math.max(-1, Math.min(1, ch[i]));
      this.buf[this.n++] = s < 0 ? s * 0x8000 : s * 0x7fff;
      if (this.n === this.buf.length) { this.port.postMessage(this.buf.buffer, [this.buf.buffer]); this.buf = new Int16Array(this.buf.length); this.n = 0; }
    }
    return true;
  }
}
registerProcessor('pcm', Pcm);`;
function toBase64(buffer) {
  const bytes = new Uint8Array(buffer); let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
function stopCapture() {
  try { stream && stream.getTracks().forEach(t => t.stop()); } catch (e) {}
  try { ctx && ctx.close(); } catch (e) {}
}
(async () => {
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false, channelCount: 1 } });
    ctx = new AudioContext();
    await ctx.audioWorklet.addModule(URL.createObjectURL(new Blob([worklet], { type: 'application/javascript' })));
    const node = new AudioWorkletNode(ctx, 'pcm', { numberOfInputs: 1, numberOfOutputs: 1, channelCount: 1, channelCountMode: 'explicit' });
    node.port.onmessage = e => post({ type: 'data', pcm: toBase64(e.data) });
    const mute = ctx.createGain(); mute.gain.value = 0;
    ctx.createMediaStreamSource(stream).connect(node).connect(mute).connect(ctx.destination); // keeps the graph pulled
    await ctx.resume();
    post({ type: 'started', sampleRate: ctx.sampleRate, label: stream.getAudioTracks()[0].label });
  } catch (e) {
    post({ type: 'error', message: (e && (e.name + ': ' + e.message)) || String(e) });
  }
})();
</script>
""";
}
