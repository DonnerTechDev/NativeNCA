using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NativeNCA.Core.Security;
using NativeNCA.Core.Services;

namespace NativeNCA.Core.Server;

/// <summary>
/// wss://127.0.0.1:13579 — the endpoint websites use to talk to NCALayer.
/// Transport only: TLS, WebSocket handshake, greeting, heartbeat. Requests go to <see cref="NcaProtocolHandler"/>.
/// </summary>
public class NativeNcaServer
{
    public const int WssPort = 13579;
    private const int MaxMessageBytes = 64 * 1024 * 1024;
    private const int MaxHandshakeBytes = 16 * 1024;
    private const string Greeting = "{\"result\":{\"version\":\"1.4\"}}";
    private const string Heartbeat = "--heartbeat--";

    private readonly TlsCertificateService _tlsService;
    private readonly IUserPromptService _promptService;
    private readonly NcaProtocolHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private SslStreamCertificateContext? _serverCert;

    public NativeNcaServer(SecureVaultService vault, TlsCertificateService tlsService, IUserPromptService promptService)
    {
        _tlsService = tlsService;
        _promptService = promptService;
        _handler = new NcaProtocolHandler(vault, promptService);
    }

    public bool IsRunning { get; private set; }

    /// <summary>Why the server could not start (null when running).</summary>
    public string? StartError { get; private set; }

    public event EventHandler? StateChanged;

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _serverCert = _tlsService.GetServerCertificateContext();
        }
        catch (Exception ex)
        {
            Fail("Ошибка сертификата TLS", ex.Message);
            return;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, WssPort);
            _listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Fail("Порт 13579 занят",
                "Скорее всего, запущен оригинальный NCALayer. Закройте его (значок в трее → Выход) и перезапустите NativeNCA.");
            return;
        }

        IsRunning = true;
        StartError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts.Cancel();
        _listener?.Stop();
        IsRunning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string title, string message)
    {
        StartError = $"{title}: {message}";
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = _promptService.ShowNotificationAsync(title, message);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken serverToken)
    {
        using (tcpClient)
        using (var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
        {
            var ct = connectionCts.Token;
            await using var ssl = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);

            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = _serverCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct);

                var handshake = await ReadHandshakeAsync(ssl, ct);
                if (handshake == null)
                {
                    await WriteAsciiAsync(ssl, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n", ct);
                    return;
                }

                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(handshake.Value.Key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await WriteAsciiAsync(ssl,
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n", ct);

                using var ws = WebSocket.CreateFromStream(ssl, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                await SendTextAsync(ws, Greeting, ct);

                var sendLock = new SemaphoreSlim(1, 1);
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    string? message = await ReceiveTextAsync(ws, ct);
                    if (message == null) break; // closed by the browser

                    if (message == Heartbeat)
                    {
                        await SendLockedAsync(ws, sendLock, Heartbeat, ct);
                        continue;
                    }

                    // Process concurrently so heartbeats keep flowing while the overlay is open.
                    // When the browser disconnects, connectionCts cancels and the overlay closes.
                    _ = Task.Run(async () =>
                    {
                        string response = await _handler.HandleAsync(message, handshake.Value.Origin, ct);
                        if (ws.State == WebSocketState.Open)
                            await SendLockedAsync(ws, sendLock, response, ct);
                    }, ct);
                }
            }
            catch
            {
                // Connection-level errors (TLS failures, abrupt disconnects) are expected; nothing to report.
            }
            finally
            {
                connectionCts.Cancel();
            }
        }
    }

    private static async Task<(string Key, string Origin)?> ReadHandshakeAsync(Stream stream, CancellationToken ct)
    {
        // Read byte by byte up to the blank line so no WebSocket bytes are consumed by a buffered reader.
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < MaxHandshakeBytes)
        {
            if (await stream.ReadAsync(one, ct) == 0) return null;
            buffer.Add(one[0]);
            int n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                break;
        }

        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal)) return null;

        string? key = null, upgrade = null;
        string origin = "Неизвестный источник";
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) upgrade = value;
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) && value.Length > 0) origin = value;
        }

        if (key == null || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)) return null;
        return (key, origin);
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return null;
            }
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxMessageBytes)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, ct);
                return null;
            }
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length).Trim();
    }

    private static async Task SendLockedAsync(WebSocket ws, SemaphoreSlim sendLock, string text, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try { await SendTextAsync(ws, text, ct); }
        finally { sendLock.Release(); }
    }

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
