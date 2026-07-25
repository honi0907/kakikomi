using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kakikomi.Services;

/// <summary>
/// LAN 遠隔操作用 HTTP + WebSocket ホスト（TcpListener 版）。
/// HttpListener の URL ACL（アクセス拒否）を避ける。
/// </summary>
public sealed class RemoteControlHost : IDisposable
{
    public static RemoteControlHost Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private RemotePreviewCapture? _preview;
    private string _webRoot = "";
    private string? _lastError;
    private bool _running;
    private int _port;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public IReadOnlyList<string> GetListenUrls()
    {
        var port = _port > 0 ? _port : AppSettings.RemoteControlPort;
        var urls = new List<string>();
        foreach (var ip in EnumerateUsableIPv4().Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"http://{ip}:{port}/");
        if (urls.Count == 0)
            urls.Add($"http://127.0.0.1:{port}/");
        return urls;
    }

    public void ApplyFromSettings()
    {
        if (AppSettings.RemoteControlEnabled)
            Start();
        else
            Stop();
    }

    public void Start()
    {
        lock (_gate)
        {
            Stop_NoLock();
            _lastError = null;
            _webRoot = Path.Combine(AppContext.BaseDirectory, "RemoteWeb");
            if (!Directory.Exists(_webRoot))
            {
                _lastError = $"RemoteWeb フォルダがありません: {_webRoot}";
                return;
            }

            var port = AppSettings.RemoteControlPort;
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
            }
            catch (Exception ex)
            {
                _lastError = $"ポート {port} で待受開始に失敗: {ex.Message}";
                return;
            }

            _listener = listener;
            _port = port;
            _cts = new CancellationTokenSource();
            _preview = new RemotePreviewCapture(OnJpegFrame);
            _preview.Start();
            // WS クライアントが付くまでプレビュー購読しない（本機映像経路の負荷軽減）
            _running = true;
            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), token);
            _ = Task.Run(() => StatusBroadcastLoopAsync(token), token);
        }
    }

    public void Stop()
    {
        lock (_gate)
            Stop_NoLock();
    }

    private void Stop_NoLock()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;

        _preview?.Dispose();
        _preview = null;
        try { RemoteNetaLoopService.Instance.Stop(); } catch { /* ignore */ }

        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();

        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        _acceptLoop = null;
        _port = 0;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Remote] accept: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }
    }

    private async Task StatusBroadcastLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var json = JsonSerializer.Serialize(RemoteControlBridge.BuildStatus(), JsonOptions);
            foreach (var client in _clients.Values)
            {
                if (client.Socket.State != WebSocketState.Open)
                    continue;
                try
                {
                    await client.SendTextAsync(json, token).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken token)
    {
        NetworkStream? stream = null;
        try
        {
            stream = tcp.GetStream();
            stream.ReadTimeout = 15000;
            var request = await ReadHttpRequestAsync(stream, token).ConfigureAwait(false);
            if (request is null)
                return;

            var path = request.Path;
            var query = request.Query;

            if (IsWebSocketUpgrade(request) &&
                path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(request.Headers, query))
                {
                    await WriteHttpAsync(stream, 401, "text/plain; charset=utf-8", "unauthorized", token)
                        .ConfigureAwait(false);
                    return;
                }

                if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key) ||
                    string.IsNullOrWhiteSpace(key))
                {
                    await WriteHttpAsync(stream, 400, "text/plain; charset=utf-8", "missing key", token)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteWebSocketHandshakeAsync(stream, key, token).ConfigureAwait(false);
                var ws = WebSocket.CreateFromStream(
                    stream,
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));

                // TcpClient/stream lifetime owned by WebSocket now — don't dispose tcp early
                tcp = null!;
                stream = null;
                await HandleWebSocketAsync(ws, token).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(request.Headers, query))
                {
                    await WriteJsonAsync(stream, 401, new { ok = false, error = "unauthorized" }, token)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(stream, 200, RemoteControlBridge.BuildStatus(), token)
                    .ConfigureAwait(false);
                return;
            }

            await ServeStaticAsync(stream, path, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Remote] client: {ex.Message}");
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* ignore */ }
            try { tcp?.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task HandleWebSocketAsync(WebSocket socket, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var session = new ClientSession(id, socket);
        _clients[id] = session;
        RefreshPreviewClientGate();

        try
        {
            await session.SendTextAsync(
                    JsonSerializer.Serialize(RemoteControlBridge.BuildStatus(), JsonOptions),
                    token)
                .ConfigureAwait(false);

            var buffer = new byte[8 * 1024];
            while (session.Socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await session.Socket
                    .ReceiveAsync(buffer, token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await RemoteControlBridge.HandleCommandAsync(json).ConfigureAwait(false);
                await session.SendTextAsync(
                        JsonSerializer.Serialize(RemoteControlBridge.BuildStatus(), JsonOptions),
                        token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Remote] ws: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            session.Dispose();
            RefreshPreviewClientGate();
        }
    }

    private void RefreshPreviewClientGate()
    {
        RemotePreviewCapture? preview;
        bool hasOpen;
        lock (_gate)
        {
            preview = _preview;
            hasOpen = false;
            foreach (var client in _clients.Values)
            {
                if (client.Socket.State == WebSocketState.Open)
                {
                    hasOpen = true;
                    break;
                }
            }
        }

        preview?.SetClientsConnected(hasOpen);
    }

    private void OnJpegFrame(byte[] jpeg)
    {
        PerfMonitorService.Instance.RecordPreviewFrame(jpeg.Length);
        foreach (var client in _clients.Values)
        {
            if (client.Socket.State != WebSocketState.Open)
                continue;
            client.TrySendBinaryAsync(jpeg);
        }
    }

    private async Task ServeStaticAsync(NetworkStream stream, string path, CancellationToken token)
    {
        if (path is "/" or "\\")
            path = "/index.html";

        path = path.Replace('\\', '/');
        if (path.Contains("..", StringComparison.Ordinal))
        {
            await WriteHttpAsync(stream, 400, "text/plain; charset=utf-8", "bad request", token)
                .ConfigureAwait(false);
            return;
        }

        var relative = path.TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(_webRoot, relative));
        if (!full.StartsWith(Path.GetFullPath(_webRoot), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full))
        {
            await WriteHttpAsync(stream, 404, "text/plain; charset=utf-8", "not found", token)
                .ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(full, token).ConfigureAwait(false);
        await WriteHttpAsync(stream, 200, GuessContentType(full), bytes, token).ConfigureAwait(false);
    }

    private static bool IsAuthorized(Dictionary<string, string> headers, string query)
    {
        var expected = AppSettings.RemoteControlPin ?? "";
        if (string.IsNullOrEmpty(expected))
            return true;

        if (headers.TryGetValue("X-Kakikomi-Pin", out var headerPin) &&
            string.Equals(headerPin, expected, StringComparison.Ordinal))
            return true;

        var pin = GetQueryValue(query, "pin");
        return string.Equals(pin, expected, StringComparison.Ordinal);
    }

    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade) &&
               upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
               request.Headers.TryGetValue("Connection", out var connection) &&
               connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteWebSocketHandshakeAsync(NetworkStream stream, string key, CancellationToken token)
    {
        const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + magic)));
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object body, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonOptions));
        await WriteHttpAsync(stream, status, "application/json; charset=utf-8", bytes, token)
            .ConfigureAwait(false);
    }

    private static Task WriteHttpAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body,
        CancellationToken token) =>
        WriteHttpAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), token);

    private static async Task WriteHttpAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] body,
        CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Error"
        };

        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<HttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token)
                .ConfigureAwait(false);
            if (read <= 0)
                break;
            total += read;
            var textSoFar = Encoding.ASCII.GetString(buffer, 0, total);
            if (textSoFar.Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        if (total == 0)
            return null;

        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return null;

        var headerPart = text[..headerEnd];
        var lines = headerPart.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
            return null;

        var target = requestLine[1];
        var qIndex = target.IndexOf('?', StringComparison.Ordinal);
        var path = qIndex >= 0 ? target[..qIndex] : target;
        var query = qIndex >= 0 ? target[(qIndex + 1)..] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[name] = value;
        }

        return new HttpRequest(path, query, headers);
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0)
                continue;
            if (!string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase))
                continue;
            return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }

        return null;
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };

    private static IEnumerable<string> EnumerateUsableIPv4()
    {
        yield return "127.0.0.1";
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("169.254.", StringComparison.Ordinal))
                    continue;
                yield return s;
            }
        }
    }

    private sealed record HttpRequest(
        string Path,
        string Query,
        Dictionary<string, string> Headers);

    private sealed class ClientSession : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _pendingGate = new();
        private byte[]? _pendingJpeg;
        private int _flushing;
        public Guid Id { get; }
        public WebSocket Socket { get; }

        public ClientSession(Guid id, WebSocket socket)
        {
            Id = id;
            Socket = socket;
        }

        public async Task SendTextAsync(string text, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Socket.State != WebSocketState.Open)
                    return;
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>最新 JPEG を1枚保持し、送信可能になったら送る（途中の古いフレームは捨てる）。</summary>
        public void TrySendBinaryAsync(byte[] data)
        {
            lock (_pendingGate)
                _pendingJpeg = data;

            if (Interlocked.Exchange(ref _flushing, 1) == 1)
                return;

            _ = FlushPendingBinaryAsync();
        }

        private async Task FlushPendingBinaryAsync()
        {
            try
            {
                while (true)
                {
                    byte[]? next;
                    lock (_pendingGate)
                    {
                        next = _pendingJpeg;
                        _pendingJpeg = null;
                    }

                    if (next is null)
                        break;

                    await _sendLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (Socket.State != WebSocketState.Open)
                            return;
                        await Socket.SendAsync(next, WebSocketMessageType.Binary, true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);

                // 送信中に新しいフレームが来ていたらもう一周
                lock (_pendingGate)
                {
                    if (_pendingJpeg is not null && Interlocked.Exchange(ref _flushing, 1) == 0)
                        _ = FlushPendingBinaryAsync();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (Socket.State == WebSocketState.Open)
                    Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .GetAwaiter().GetResult();
            }
            catch
            {
                // ignore
            }

            try { Socket.Dispose(); } catch { /* ignore */ }
            _sendLock.Dispose();
        }
    }
}
