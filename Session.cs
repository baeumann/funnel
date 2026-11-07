using System.Net.Sockets;
using funnel_.protocol;

namespace funnel_.core;

public sealed record PacketInfo(Direction Direction, ConnectionState State, int Id, string? Name, byte[] Payload);

public sealed class ProxyOptions
{
    public volatile bool LogPackets;
    public volatile bool ShowChat = true;
    public string? LogFilter;
}

/// <summary>
/// One Minecraft client connected through the proxy, together with its connection to the real server.
///
/// Both directions are forwarded packet by packet (not byte by byte), so extra packets can be inserted
/// between two forwarded ones without ever corrupting the stream. The session follows the protocol state
/// machine (handshake, login, configuration, play) and the compression threshold, which is what makes
/// parsing and injecting possible.
///
/// Limitation: on online-mode servers everything after the Encryption Response is AES encrypted with a key
/// negotiated between client and server. The proxy cannot read or inject there, so the session falls back to
/// a plain byte pipe (the game keeps working, the extra features do not).
/// </summary>
public sealed class Session : IDisposable
{
    private static int _counter;

    private readonly TcpClient _client;
    private readonly TcpClient _server;
    private readonly NetworkStream _clientStream;
    private readonly NetworkStream _serverStream;
    private readonly SemaphoreSlim _clientGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _serverGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly CompressionState _compression = new CompressionState();
    private readonly ProxyOptions _options;
    private readonly string _targetHost;
    private readonly int _targetPort;

    private volatile ConnectionState _state = ConnectionState.Handshake;
    private volatile bool _encrypted;
    private int _disposed;

    public int Number { get; } = Interlocked.Increment(ref _counter);
    public PacketIds Ids { get; }
    public GameState Game { get; } = new GameState();
    public Bot Bot { get; }

    public int ProtocolVersion { get; private set; }
    public string? Username { get; private set; }
    public ConnectionState State => _state;
    public bool Encrypted => _encrypted;
    public int CompressionThreshold => _compression.Threshold;

    /// <summary>Raised on a proxy thread for every packet seen in either direction.</summary>
    public event Action<Session, PacketInfo>? PacketReceived;

    public Session(TcpClient client, TcpClient server, PacketIds ids, ProxyOptions options, string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _client = client;
        _server = server;
        _clientStream = client.GetStream();
        _serverStream = server.GetStream();
        Ids = ids;
        _options = options;
        Bot = new Bot(this);
    }

    public async Task RunAsync()
    {
        var up = PumpAsync(Direction.Serverbound);
        var down = PumpAsync(Direction.Clientbound);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        Dispose();
        await Task.WhenAll(up, down).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _client.Close();
        _server.Close();
    }

    // ---------------------------------------------------------------- injection

    /// <summary>Sends a packet with an explicit numeric ID. Works in the configuration and play states.</summary>
    public async Task SendRawAsync(Direction direction, int id, byte[] payload)
    {
        if (_encrypted)
        {
            throw new InvalidOperationException("this connection is encrypted (online-mode server); packets cannot be injected");
        }
        if (_state != ConnectionState.Play && _state != ConnectionState.Configuration)
        {
            throw new InvalidOperationException("can only inject packets in the play/configuration state, current state is " + _state);
        }

        var frame = PacketFramer.Encode(id, payload, _compression.Threshold);
        if (direction == Direction.Serverbound)
        {
            await WriteAsync(_serverStream, _serverGate, frame).ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(_clientStream, _clientGate, frame).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a packet identified by name (first known alias wins) using the loaded ID table.</summary>
    public async Task SendAsync(Direction direction, byte[] payload, params string[] names)
    {
        var id = Ids.Find(_state, direction, names);
        if (id == null)
        {
            throw new InvalidOperationException(
                "no packet ID known for " + string.Join("/", names) + " (" + direction + ", " + _state + "); load a packet ID file with --ids=");
        }
        await SendRawAsync(direction, id.Value, payload).ConfigureAwait(false);
    }

    private async Task WriteAsync(NetworkStream destination, SemaphoreSlim gate, ReadOnlyMemory<byte> data)
    {
        await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await destination.WriteAsync(data, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------------------------------------------------------------- forwarding

    private async Task PumpAsync(Direction direction)
    {
        var source = direction == Direction.Serverbound ? _clientStream : _serverStream;
        var destination = direction == Direction.Serverbound ? _serverStream : _clientStream;
        var gate = direction == Direction.Serverbound ? _serverGate : _clientGate;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int first = await StreamUtil.ReadByteAsync(source, ct).ConfigureAwait(false);

                if (_encrypted)
                {
                    // Encryption started: from here on the bytes are opaque, so just pipe them through.
                    await WriteAsync(destination, gate, new[] { (byte)first }).ConfigureAwait(false);
                    await CopyRawAsync(source, destination, gate, ct).ConfigureAwait(false);
                    return;
                }

                var frame = await PacketFramer.ReadAsync(source, first, _compression, ct).ConfigureAwait(false);
                var forward = Observe(direction, frame);
                await WriteAsync(destination, gate, forward).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException)
        {
            if (_disposed == 0)
            {
                Say((direction == Direction.Serverbound ? "the game" : "the server") + " closed the connection (state " + _state + ")");
            }
        }
        catch (IOException ex)
        {
            if (_disposed == 0)
            {
                Say("connection to " + (direction == Direction.Serverbound ? "the game" : "the server") + " lost in state " + _state + ": " + ex.Message);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Say(direction + " connection error: " + ex.Message);
        }
    }

    private async Task CopyRawAsync(NetworkStream source, NetworkStream destination, SemaphoreSlim gate, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            await WriteAsync(destination, gate, buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- protocol state machine

    /// <summary>Inspects a frame and returns the bytes to forward (the frame itself, except for the handshake).</summary>
    private byte[] Observe(Direction direction, Frame frame)
    {
        if (frame.Id < 0)
        {
            return frame.Raw;
        }

        var state = _state;
        string? name = null;
        byte[] forward = frame.Raw;

        try
        {
            switch (state)
            {
                case ConnectionState.Handshake:
                    if (direction == Direction.Serverbound && frame.Id == 0)
                    {
                        forward = ObserveHandshake(frame);
                    }
                    break;

                case ConnectionState.Login:
                    name = ObserveLogin(direction, frame);
                    break;

                case ConnectionState.Configuration:
                    name = Ids.NameOf(state, direction, frame.Id);
                    if (direction == Direction.Serverbound && name == "finish_configuration")
                    {
                        ChangeState(ConnectionState.Play);
                    }
                    break;

                case ConnectionState.Play:
                    name = Ids.NameOf(state, direction, frame.Id);
                    if (name != null)
                    {
                        Game.Apply(direction, name, frame.Payload, ProtocolVersion);
                        if (direction == Direction.Serverbound && name == "configuration_acknowledged")
                        {
                            ChangeState(ConnectionState.Configuration);
                        }
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException)
        {
            // A packet we could not parse must never break forwarding.
        }

        Report(direction, state, frame, name);
        return forward;
    }

    private byte[] ObserveHandshake(Frame frame)
    {
        var r = new PacketReader(frame.Payload);
        ProtocolVersion = r.ReadVarInt();
        string address = r.ReadString();
        r.ReadUShort();
        int next = r.ReadVarInt();

        // 1 = status, 2 = login, 3 = transfer (1.20.5+, handled like login)
        ChangeState(next == 1 ? ConnectionState.Status : ConnectionState.Login);

        // The client wrote the address it was told to connect to ("localhost"). Servers such as Hypixel check
        // that field and refuse anything but their own domain, so present the real target instead. A suffix after
        // a NUL byte (Forge marker) is kept.
        int marker = address.IndexOf('\0');
        string suffix = marker >= 0 ? address.Substring(marker) : string.Empty;
        string newAddress = _targetHost.ToLowerInvariant() + suffix;

        if (next != 1)
        {
            Say("client handshake: protocol " + ProtocolVersion + ", address '" + address.Replace("\0", "\\0") + "' -> '" + newAddress.Replace("\0", "\\0") + "'");
        }

        var payload = new PacketWriter()
            .WriteVarInt(ProtocolVersion)
            .WriteString(newAddress)
            .WriteUShort((ushort)_targetPort)
            .WriteVarInt(next)
            .ToArray();
        return PacketFramer.Encode(0, payload, -1);
    }

    private string? ObserveLogin(Direction direction, Frame frame)
    {
        var r = new PacketReader(frame.Payload);

        if (direction == Direction.Clientbound)
        {
            switch (frame.Id)
            {
                case 0:
                    try
                    {
                        Say("server refused the login: " + ChatText.FromJson(r.ReadString()));
                    }
                    catch (Exception ex) when (ex is EndOfStreamException || ex is System.Text.Json.JsonException)
                    {
                        Say("server refused the login (reason could not be read)");
                    }
                    return "login_disconnect";
                case 1:
                    Say("server wants encryption (online-mode): inspection and injection are not possible for this connection");
                    return "hello";
                case 2:
                    Game.Uuid = r.ReadUuid();
                    Username = r.ReadString();
                    if (ProtocolVersion < 764)
                    {
                        ChangeState(ConnectionState.Play);
                    }
                    return "login_finished";
                case 3:
                    _compression.Threshold = r.ReadVarInt();
                    return "login_compression";
                case 4: return "custom_query";
                default: return null;
            }
        }

        switch (frame.Id)
        {
            case 0:
                Username = r.ReadString();
                return "hello";
            case 1:
                // Encryption Response: everything after this packet is encrypted.
                _encrypted = true;
                return "key";
            case 2: return "custom_query_answer";
            case 3:
                if (ProtocolVersion >= 764)
                {
                    ChangeState(ConnectionState.Configuration);
                    if (Ids.IsEmpty)
                    {
                        Say("no packet ID table loaded: connection is forwarded, but packets cannot be named, decoded or injected by name");
                    }
                }
                return "login_acknowledged";
            default: return null;
        }
    }

    private void ChangeState(ConnectionState state)
    {
        _state = state;
        if (state == ConnectionState.Play)
        {
            Say("play state reached" + (Username != null ? " (player " + Username + ", protocol " + ProtocolVersion + ")" : ""));
        }
    }

    private void Report(Direction direction, ConnectionState state, Frame frame, string? name)
    {
        if (_options.LogPackets)
        {
            string label = name ?? "?";
            string idText = "0x" + frame.Id.ToString("X2");
            var filter = _options.LogFilter;
            if (filter == null
                || label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || idText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[{Number}] {(direction == Direction.Serverbound ? "C->S" : "S->C")} {state,-13} {label,-28} id={idText} len={frame.Payload.Length}");
            }
        }

        var handler = PacketReceived;
        if (handler != null)
        {
            try
            {
                handler(this, new PacketInfo(direction, state, frame.Id, name, frame.Payload));
            }
            catch (Exception ex)
            {
                Say("packet handler failed: " + ex.Message);
            }
        }
    }

    private void Say(string message)
    {
        Console.WriteLine(">> [" + Number + "] " + message);
    }
}
