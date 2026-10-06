using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace MicroRecord;

/// <summary>
/// Captures the microphone in the user's own default browser. MicroRecord serves a tiny page on
/// 127.0.0.1 (a secure context, so getUserMedia works), opens it in a tab, and the page streams
/// 16-bit mono PCM back over a WebSocket.
/// This is for machines where endpoint security (e.g. Kaspersky audio input control) rejects every
/// audio stream opened by MicroRecord.exe or its child processes but trusts the installed browser.
/// The tab must stay open while recording.
/// </summary>
internal sealed class BrowserMicSource : IAudioSource
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60); // time for the user to click "Allow"
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Action<string> log;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly CancellationTokenSource cts = new();
    private readonly TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpListener? listener;
    private WebSocket? socket;
    private WaveFormat? waveFormat;
    private int port;

    public BrowserMicSource(Action<string> log) => this.log = log;

    public string Name => "browser tab getUserMedia";
    public WaveFormat WaveFormat => waveFormat ?? throw new InvalidOperationException("Not started.");
    public AudioDataHandler? Sink { get; set; }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);

        var url = $"http://127.0.0.1:{port}/?t={token}";
        log($"mic: opening {url} in the default browser");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        if (!started.Task.Wait(StartTimeout))
        {
            throw new TimeoutException($"The browser tab did not start the microphone within {StartTimeout.TotalSeconds:0}s (permission not granted?).");
        }
        waveFormat = new WaveFormat(started.Task.GetAwaiter().GetResult(), 16, 1);
    }

    private async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(cts.Token); }
            catch { return; }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var (path, headers) = await ReadRequestHead(stream);
            var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
            var authorized = query == "t=" + token;

            if (authorized && path.StartsWith("/ws?") && headers.TryGetValue("sec-websocket-key", out var key)
                && headers.TryGetValue("origin", out var origin) && origin == $"http://127.0.0.1:{port}")
            {
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
                await WriteAscii(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
                var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(20));
                if (Interlocked.CompareExchange(ref socket, ws, null) != null)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "already connected", CancellationToken.None);
                    return;
                }
                await ReceiveLoop(ws);
                return;
            }

            if (authorized && path.StartsWith("/?"))
            {
                var body = Encoding.UTF8.GetBytes(PageHtml);
                await WriteAscii(stream, $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(body);
            }
            else
            {
                await WriteAscii(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
        }
        catch (Exception ex) { log("mic: browser connection error: " + ex.Message); }
        finally { client.Dispose(); }
    }

    private async Task ReceiveLoop(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (!cts.IsCancellationRequested) log("mic: browser tab disconnected — microphone track ends here");
                started.TrySetException(new InvalidOperationException("Browser tab closed before the microphone started."));
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                Sink?.Invoke(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
            else
            {
                OnControlMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
            message.SetLength(0);
        }
    }

    private void OnControlMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "started":
                var rate = root.GetProperty("sampleRate").GetInt32();
                log($"mic: browser capture from '{root.GetProperty("label").GetString()}' at {rate}Hz");
                started.TrySetResult(rate);
                break;
            case "error":
                var message = root.GetProperty("message").GetString();
                log("mic: browser page error: " + message);
                started.TrySetException(new InvalidOperationException("Browser getUserMedia failed: " + message));
                break;
        }
    }

    private static async Task<(string Path, Dictionary<string, string> Headers)> ReadRequestHead(NetworkStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(one) == 0 || head.Length > 16 * 1024) throw new IOException("Malformed HTTP request.");
            head.Append((char)one[0]);
        }
        var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2 || requestLine[0] != "GET") throw new IOException("Unsupported HTTP request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return (requestLine[1], headers);
    }

    private static Task WriteAscii(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Stop() => Shutdown();

    public void Dispose() => Shutdown();

    private void Shutdown()
    {
        if (cts.IsCancellationRequested) return;
        cts.Cancel();
        var ws = socket;
        if (ws != null && ws.State == WebSocketState.Open)
        {
            try
            {
                ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("stop")), WebSocketMessageType.Text, true, CancellationToken.None).Wait(1000);
                ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stopped", CancellationToken.None).Wait(1000);
            }
            catch { }
        }
        try { listener?.Stop(); } catch { }
    }

    // getUserMedia -> AudioWorklet -> 16-bit mono PCM in ~100 ms binary WebSocket frames.
    // Browser AGC + noise suppression stay on (as in a meeting tab): raw headset mics such as the
    // Logitech H340 peak around -45 dBFS, which is inaudible in the mix. Echo cancellation is off.
    private const string PageHtml = """
<!doctype html>
<meta charset="utf-8">
<title>MicroRecord — микрофон</title>
<style>body{font:16px system-ui,sans-serif;margin:40px;color:#222}#s{font-size:22px;margin:16px 0}</style>
<h1>MicroRecord</h1>
<div id="s">Разрешите доступ к микрофону…</div>
<button id="go" style="display:none;font-size:20px;padding:8px 20px">Начать запись микрофона</button>
<div>Уровень: <meter id="lvl" min="0" max="1" value="0" style="width:300px"></meter> <span id="db"></span></div>
<p>Не закрывайте эту вкладку, пока идёт запись. Остановка — Ctrl+Alt+R.</p>
<script>
const status = t => document.getElementById('s').textContent = t;
const token = new URLSearchParams(location.search).get('t');
const ws = new WebSocket(`ws://127.0.0.1:${location.port}/ws?t=${token}`);
ws.binaryType = 'arraybuffer';
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
function stopCapture(text) {
  try { stream && stream.getTracks().forEach(t => t.stop()); } catch (e) {}
  try { ctx && ctx.close(); } catch (e) {}
  status(text);
}
ws.onmessage = e => { if (e.data === 'stop') stopCapture('Запись остановлена. Вкладку можно закрыть.'); };
ws.onclose = () => stopCapture('Соединение с MicroRecord закрыто. Вкладку можно закрыть.');
ws.onopen = async () => {
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: true, autoGainControl: true, channelCount: 1 } });
    ctx = new AudioContext();
    await ctx.audioWorklet.addModule(URL.createObjectURL(new Blob([worklet], { type: 'application/javascript' })));
    const node = new AudioWorkletNode(ctx, 'pcm', { numberOfInputs: 1, numberOfOutputs: 1, channelCount: 1, channelCountMode: 'explicit' });
    node.port.onmessage = e => {
      const pcm = new Int16Array(e.data); let peak = 0;
      for (let i = 0; i < pcm.length; i++) peak = Math.max(peak, Math.abs(pcm[i]));
      document.getElementById('lvl').value = peak ? Math.max(0, 1 + Math.log10(peak / 32768) / 3) : 0; // -60..0 dB
      document.getElementById('db').textContent = peak ? (20 * Math.log10(peak / 32768)).toFixed(0) + ' dB' : 'тишина';
      if (ws.readyState === 1) ws.send(e.data);
    };
    const mute = ctx.createGain(); mute.gain.value = 0;
    ctx.createMediaStreamSource(stream).connect(node).connect(mute).connect(ctx.destination); // keeps the graph pulled
    // Autoplay policy may keep the AudioContext suspended until the user clicks in the page;
    // a suspended context produces no samples at all.
    const resumeSoon = () => Promise.race([ctx.resume(), new Promise(r => setTimeout(r, 500))]);
    await resumeSoon();
    while (ctx.state !== 'running') {
      status('Браузер ждёт клика, чтобы включить звук:');
      const go = document.getElementById('go'); go.style.display = '';
      await new Promise(r => go.onclick = r);
      go.style.display = 'none';
      await resumeSoon();
    }
    const track = stream.getAudioTracks()[0];
    ws.send(JSON.stringify({ type: 'started', sampleRate: ctx.sampleRate, label: track.label + (track.muted ? ' [track muted]' : '') + ' ctx=' + ctx.state }));
    status('● Идёт запись микрофона');
  } catch (e) {
    const message = (e && (e.name + ': ' + e.message)) || String(e);
    ws.send(JSON.stringify({ type: 'error', message }));
    status('Ошибка: ' + message);
  }
};
</script>
""";
}
