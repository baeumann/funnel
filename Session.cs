using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using funnel_.protocol;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

using NetMemory = System.Memory<byte>;
using NetReadOnlyMemory = System.ReadOnlyMemory<byte>;
using NetValueTask = System.Threading.Tasks.ValueTask;
using NetValueTaskInt = System.Threading.Tasks.ValueTask<int>;

namespace funnel_.core;

public sealed record PacketInfo(Direction Direction, ConnectionState State, int Id, string? Name, byte[] Payload);

public sealed class ProxyOptions
{
    public volatile bool LogPackets;
    public volatile bool ShowChat = true;
    public string? LogFilter;
}

public sealed class Session : IDisposable
{
    private static int _counter;
    private static readonly HttpClient _httpClient = new();
    private readonly TcpClient _client, _server;
    private Stream _clientSourceStream, _clientDestinationStream, _serverSourceStream, _serverDestinationStream;
    private readonly SemaphoreSlim _clientGate = new(1, 1), _serverGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly CompressionState _compression = new();
    private readonly ProxyOptions _options;
    private readonly string _targetHost;
    private readonly int _targetPort;

    private volatile ConnectionState _state = ConnectionState.Handshake;
    private volatile bool _encrypted;
    private int _disposed;
    private bool _isFirstLoginPacket = true;

    // Microsoft Online-Auth Daten (Zuvor über CmlLib geholt)
    public static volatile string MicrosoftAccessToken = "";
    public static volatile string MicrosoftUuid = "";

    private readonly RSA _proxyRsa;
    private byte[]? _serverPublicKeyBytes, _verifyToken;
    private string _serverId = "";

    public int Number { get; } = Interlocked.Increment(ref _counter);
    public PacketIds Ids { get; }
    public GameState Game { get; } = new();
    public Bot Bot { get; }
    public int ProtocolVersion { get; private set; }
    public string? Username { get; private set; }
    public ConnectionState State => _state;
    public bool Encrypted => _encrypted;
    public int CompressionThreshold => _compression.Threshold;
    public event Action<Session, PacketInfo>? PacketReceived;

    public Session(TcpClient client, TcpClient server, PacketIds ids, ProxyOptions options, string targetHost, int targetPort)
    {
        _targetHost = targetHost; _targetPort = targetPort; _client = client; _server = server;
        _clientSourceStream = client.GetStream(); _clientDestinationStream = client.GetStream();
        _serverSourceStream = server.GetStream(); _serverDestinationStream = server.GetStream();
        Ids = ids; _options = options; Bot = new Bot(this);
        _proxyRsa = RSA.Create(1024);
    }

    public async Task RunAsync()
    {
        var up = PumpAsync(Direction.Serverbound); var down = PumpAsync(Direction.Clientbound);
        await Task.WhenAny(up, down).ConfigureAwait(false); Dispose();
        await Task.WhenAll(up, down).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _client.Close(); _server.Close(); _proxyRsa.Dispose();
    }

    public async Task SendRawAsync(Direction direction, int id, byte[] payload)
    {
        if (_state != ConnectionState.Play && _state != ConnectionState.Configuration)
            throw new InvalidOperationException("Invalid state for injection: " + _state);
        byte[] frameBytes = PacketFramer.Encode(id, payload, _compression.Threshold);
        if (direction == Direction.Serverbound) await WriteBytesAsync(_serverDestinationStream, _serverGate, frameBytes).ConfigureAwait(false);
        else await WriteBytesAsync(_clientDestinationStream, _clientGate, frameBytes).ConfigureAwait(false);
    }

    public async Task SendAsync(Direction direction, byte[] payload, params string[] names)
    {
        var id = Ids.Find(_state, direction, names);
        if (id == null) throw new InvalidOperationException($"No packet ID known for {string.Join("/", names)}");
        await SendRawAsync(direction, id.Value, payload).ConfigureAwait(false);
    }

    private async Task WriteBytesAsync(Stream destination, SemaphoreSlim gate, NetReadOnlyMemory data)
    {
        await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await destination.WriteAsync(data, _cts.Token).ConfigureAwait(false); } finally { gate.Release(); }
    }

    private async Task WriteFrameAsync(Stream destination, SemaphoreSlim gate, Frame frame)
    {
        await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await destination.WriteAsync(frame.Raw, _cts.Token).ConfigureAwait(false); } finally { gate.Release(); }
    }

        private async Task PumpAsync(Direction direction)
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var source = direction == Direction.Serverbound ? _clientSourceStream : _serverSourceStream;
                var destination = direction == Direction.Serverbound ? _serverDestinationStream : _clientDestinationStream;
                var gate = direction == Direction.Serverbound ? _serverGate : _clientGate;

                int first = await StreamUtil.ReadByteAsync(source, ct).ConfigureAwait(false);

                // =========================================================================
                // ONLINE-MODE KRYPTO-BOOTSTRAP (Mit synchronem HTTP-Await & RAM-Fix)
                // =========================================================================
                if (_state == ConnectionState.Login && direction == Direction.Serverbound && !_encrypted && _serverPublicKeyBytes != null)
                {
                    byte[] tempBuffer = new byte[first]; int totalRead = 0;
                    while (totalRead < first)
                    {
                        int r = await source.ReadAsync(tempBuffer.AsMemory(totalRead, first - totalRead), ct).ConfigureAwait(false);
                        if (r <= 0) throw new EndOfStreamException();
                        totalRead += r;
                    }

                    var bootstrapReader = new PacketReader(tempBuffer);
                    int packetId = bootstrapReader.ReadVarInt();

                    if (packetId == 0x01)
                    {
                        int secretLength = bootstrapReader.ReadVarInt();
                        byte[] encryptedSharedSecret = bootstrapReader.ReadBytes(secretLength);
                        int tokenLength = bootstrapReader.ReadVarInt();
                        byte[] encryptedVerifyToken = bootstrapReader.ReadBytes(tokenLength);

                        Say("MITM: Bootstrap Intercepted Encryption Response. Decrypting Shared Secret...");
                        byte[] sharedSecret = _proxyRsa.Decrypt(encryptedSharedSecret, RSAEncryptionPadding.Pkcs1);
                        byte[] verifyToken = _proxyRsa.Decrypt(encryptedVerifyToken, RSAEncryptionPadding.Pkcs1);

                        // RAM-FIX: Greift direkt atomar auf die thread-sicheren volatile Variablen zu
                        if (!string.IsNullOrEmpty(MicrosoftAccessToken))
                        {
                            Say("MITM: Online tokens actively verified in shared memory. Authenticating hash with Mojang...");
                            string mcHash = CalculateMinecraftHash(_serverId, sharedSecret, _serverPublicKeyBytes);

                            var payload = new { accessToken = MicrosoftAccessToken, selectedProfile = MicrosoftUuid.Replace("-", ""), serverId = mcHash };
                            
                            // Synchroner Request an das offizielle Mojang-Backend (Wartet auf Bestätigung)
                            var res = await _httpClient.PostAsJsonAsync("https://mojang.com", payload, ct).ConfigureAwait(false);
                            
                            if (res.IsSuccessStatusCode) 
                            {
                                Say("[Auth] Mojang Session successfully verified! Resuming network stream.");
                            }
                            else 
                            {
                                Say($"[Auth] CRITICAL: Mojang rejected session: {await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
                                throw new InvalidOperationException("Mojang rejected authentication token.");
                            }
                        }

                        Say("MITM: Re-encrypting Shared Secret for target Online-Mode Server...");
                        var serverRsaKey = PublicKeyFactory.CreateKey(_serverPublicKeyBytes);
                        var serverRsaEngine = new Org.BouncyCastle.Crypto.Encodings.Pkcs1Encoding(new RsaEngine());
                        serverRsaEngine.Init(true, serverRsaKey);
                        byte[] reEncryptedSharedSecret = serverRsaEngine.ProcessBlock(sharedSecret, 0, sharedSecret.Length);
                        byte[] reEncryptedVerifyToken = serverRsaEngine.ProcessBlock(verifyToken, 0, verifyToken.Length);

                        var bootstrapWriter = new PacketWriter();
                        bootstrapWriter.WriteVarInt(reEncryptedSharedSecret.Length);
                        bootstrapWriter.WriteBytes(reEncryptedSharedSecret);
                        bootstrapWriter.WriteVarInt(reEncryptedVerifyToken.Length);
                        bootstrapWriter.WriteBytes(reEncryptedVerifyToken);

                        byte[] forwardPayload = bootstrapWriter.ToArray();
                        byte[] forwardRaw = PacketFramer.Encode(packetId, forwardPayload, -1);

                        EnableEncryption(sharedSecret);
                        await WriteBytesAsync(destination, gate, forwardRaw).ConfigureAwait(false);
                        continue;
                    }
                }

                Frame frame = await PacketFramer.ReadAsync(source, first, _compression, ct).ConfigureAwait(false);
                Frame forwardFrame = ObserveAndIntercept(direction, frame);
                await WriteFrameAsync(destination, gate, forwardFrame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { if (_disposed == 0) Say((direction == Direction.Serverbound ? "the game" : "the server") + " closed connection"); }
        catch (IOException ex) { if (_disposed == 0) Say("Connection lost: " + ex.Message); }
        catch (Exception ex) { Say(direction + " error: " + ex.Message); }
    }



private Frame ObserveAndIntercept(Direction direction, Frame frame)
{
    int packetId = frame.Id;
    byte[] payload = frame.Payload;

    // =========================================================================
    // VERSIONSUNABHÄNGIGER HANDSHAKE-PARSER (AKTIVIERT - Löst den Krypto-Abbruch)
    // =========================================================================
    if (_state == ConnectionState.Handshake && direction == Direction.Serverbound && packetId == 0x00)
    {
        Say($"MITM: Intercepted Handshake. Patching port to {_targetPort}");
        
        try
        {
            using var handshakeMs = new MemoryStream(payload);
            int clientProtocolVersion = MinecraftStreamUtil.ReadVarInt(handshakeMs);
            string serverAddress = MinecraftStreamUtil.ReadString(handshakeMs);
            
            // Port wird im Handshake standardmäßig als Unsigned Short (2 Bytes) übertragen
            int serverPort = (handshakeMs.ReadByte() << 8) | handshakeMs.ReadByte();
            
            // Das darauffolgende VarInt im Netzwerkstrom bestimmt den Folgestatus (1 = Status, 2 = Login)
            int nextStateInt = MinecraftStreamUtil.ReadVarInt(handshakeMs);

            // Port-Patch auf Netzwerk-Ebene für den Ziel-Server vorbereiten
            byte[] patchedRaw = new byte[frame.Raw.Length];
            System.Buffer.BlockCopy(frame.Raw, 0, patchedRaw, 0, frame.Raw.Length);

            if (patchedRaw[patchedRaw.Length - 3] == 0x63 && patchedRaw[patchedRaw.Length - 2] == 0xDD)
            {
                patchedRaw[patchedRaw.Length - 2] = 0xE7; // Patcht 25565 zu 25575
            }
            else
            {
                patchedRaw[patchedRaw.Length - 3] = (byte)(_targetPort >> 8);
                patchedRaw[patchedRaw.Length - 2] = (byte)(_targetPort & 0xFF);
            }

            ProtocolVersion = clientProtocolVersion;
            
            // Schaltet den internen Proxy-Status absolut synchron zur Java-Engine um
            _state = nextStateInt == 1 ? ConnectionState.Status : ConnectionState.Login;
            
            Say($"[Protocol] Handshake processed. Protocol: {ProtocolVersion}. State switched to: {_state}");
            return new Frame(packetId, payload, patchedRaw);
        }
        catch (Exception ex)
        {
            Say($"[Protocol] Warning: Failed to parse extended handshake packet: {ex.Message}. Falling back to byte patch.");
        }
    }

    // Reines Durchreichen des Login-Starts bei Online-Verbindungen
    if (_state == ConnectionState.Login && direction == Direction.Serverbound && packetId == 0x00 && _isFirstLoginPacket)
    {
        _isFirstLoginPacket = false;
        using var ms = new MemoryStream(payload);
        Username = MinecraftStreamUtil.ReadString(ms);
        Say($"MITM: Processing Legit Login Start from {Username}...");
    }

    Frame modifiedFrame = Observe(direction, frame);
    packetId = modifiedFrame.Id;
    payload = modifiedFrame.Payload;

    string? packetName = Ids.NameOf(_state, direction, packetId);
    PacketReceived?.Invoke(this, new PacketInfo(direction, _state, packetId, packetName, payload));

    // =========================================================================
    // ENCRYPTION REQUEST INTERZEPTION (Sichert ServerID & Keys für den Join)
    // =========================================================================
    if (_state == ConnectionState.Login && direction == Direction.Clientbound && packetId == 0x01)
    {
        Say("MITM: Intercepted Encryption Request from Online Server. Swapping Keys...");
        using var ms = new MemoryStream(payload);
        _serverId = MinecraftStreamUtil.ReadString(ms);
        _serverPublicKeyBytes = MinecraftStreamUtil.ReadByteArray(ms);
        _verifyToken = MinecraftStreamUtil.ReadByteArray(ms);
        
        byte[] proxyPublicKeyBytes = _proxyRsa.ExportSubjectPublicKeyInfo();
        
        using var outMs = new MemoryStream();
        MinecraftStreamUtil.WriteString(outMs, _serverId);
        MinecraftStreamUtil.WriteByteArray(outMs, proxyPublicKeyBytes);
        MinecraftStreamUtil.WriteByteArray(outMs, _verifyToken);
        
        byte[] modPayload = outMs.ToArray();
        byte[] modRaw = PacketFramer.Encode(packetId, modPayload, -1);
        return new Frame(packetId, modPayload, modRaw);
    }

    return modifiedFrame;
}


    private void EnableEncryption(byte[] sharedSecret)
    {
        var rawClient = new InjectorStream(_client.GetStream());
        var rawServer = new InjectorStream(_server.GetStream());
        _clientSourceStream = new MinecraftAesStream(rawClient, sharedSecret, false);
        _clientDestinationStream = new MinecraftAesStream(rawClient, sharedSecret, true);
        _serverSourceStream = new MinecraftAesStream(rawServer, sharedSecret, false);
        _serverDestinationStream = new MinecraftAesStream(rawServer, sharedSecret, true);
        _encrypted = true; Say("MITM: AES-CFB8 Crypto Pipeline securely attached to both sockets.");
    }
    private Frame Observe(Direction direction, Frame frame)
{
    int packetId = frame.Id;
    byte[] payload = frame.Payload;

    // =========================================================================
    // STANDARD STATE-MACHINE (Zustands-Synchronisation)
    // =========================================================================
    if (_state == ConnectionState.Handshake && direction == Direction.Serverbound && packetId == 0x00)
        _state = payload[payload.Length - 1] == 1 ? ConnectionState.Status : ConnectionState.Login;
    else if (_state == ConnectionState.Login && direction == Direction.Clientbound)
    {
        string? name = Ids.NameOf(_state, direction, packetId);
        if (name == "set_compression" || name == "login_compression")
        {
            using var ms = new MemoryStream(payload);
            _compression.Threshold = MinecraftStreamUtil.ReadVarInt(ms);
        }
        else if (name == "login_success" || name == "success") _state = ConnectionState.Configuration;
    }
    else if (_state == ConnectionState.Configuration)
    {
        string? name = Ids.NameOf(_state, direction, packetId);
        if (direction == Direction.Clientbound && name == "finish_configuration") _state = ConnectionState.Play;
        else if (direction == Direction.Serverbound && name == "finish_configuration") _state = ConnectionState.Play;
    }
    
    // =========================================================================
    // MITM CHAT-FILTER (Aktiv im PLAY-Zustand)
    // =========================================================================
    else if (_state == ConnectionState.Play && direction == Direction.Serverbound)
    {
        string? name = Ids.NameOf(_state, direction, packetId);
        
        // Fängt das offizielle Chat-Sendepaket des Clients ab (ID variiert je nach Build, Name bleibt stabil)
        if (name == "chat_message" || name == "chat" || name == "send_chat")
        {
            try
            {
                using var ms = new MemoryStream(payload);
                string originalMessage = MinecraftStreamUtil.ReadString(ms);
                
                // Ausgabe im Proxy-Terminal
                Console.WriteLine($"\n[Chat-Intercept] Spieler {Username ?? "Spieler"} schreibt: {originalMessage}");
                
                // Modifikation: Wir hängen einen Proxy-Tag an die Nachricht an!
                string modifiedMessage = originalMessage + " [via Funnel Proxy]";
                
                // Wir bauen den Payload konform zur Mojang-Spezifikation neu auf
                using var outMs = new MemoryStream();
                MinecraftStreamUtil.WriteString(outMs, modifiedMessage);
                
                // Minecraft verlangt ab 1.19+ zusätzliche Zeitstempel und Krypto-Signaturen im Chat.
                // Da wir das Paket unverschlüsselt manipulieren, kopieren wir die restlichen Signatur-Bytes
                // (wie den Salt und die Signatur-Länge), die nach der Nachricht im Payload übrig sind.
                byte[] remainder = new byte[payload.Length - ms.Position];
                ms.Read(remainder, 0, remainder.Length);
                outMs.Write(remainder, 0, remainder.Length);
                
                byte[] modifiedPayload = outMs.ToArray();
                byte[] modifiedRaw = PacketFramer.Encode(packetId, modifiedPayload, _compression.Threshold);
                
                Console.WriteLine($"[Chat-Intercept] Nachricht erfolgreich manipuliert zu: {modifiedMessage}\n");
                return new Frame(packetId, modifiedPayload, modifiedRaw);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Chat-Intercept] Fehler beim Parsen der Chat-Nachricht: {ex.Message}");
            }
        }
    }

    return frame;
}

        private static string CalculateMinecraftHash(string serverId, byte[] sharedSecret, byte[] serverPublicKey)
    {
        using var sha1 = SHA1.Create();
        using var ms = new MemoryStream();
        
        byte[] serverIdBytes = Encoding.ASCII.GetBytes(serverId);
        ms.Write(serverIdBytes, 0, serverIdBytes.Length);
        ms.Write(sharedSecret, 0, sharedSecret.Length);
        ms.Write(serverPublicKey, 0, serverPublicKey.Length);
        
        byte[] hash = sha1.ComputeHash(ms.ToArray());
        
        // EXAKTER MINECRAFT-VORZEICHEN-FIX:
        // Prüfe das allererste Bit des ersten Bytes (hash[0]) auf das Vorzeichen (0x80).
        bool isNegative = (hash[0] & 0x80) == 0x80;
        
        if (isNegative)
        {
            // Erzeuge das Zweierkomplement durch Invertieren und Addition von 1
            bool carry = true;
            for (int i = hash.Length - 1; i >= 0; i--)
            {
                hash[i] = (byte)~hash[i];
                if (carry) 
                { 
                    carry = hash[i] == 0xFF; 
                    hash[i]++; 
                }
            }
        }

        // Konvertiere in Hex-String, entferne Bindestriche, mache es lowercase und trimme führende Nullen
        string hex = BitConverter.ToString(hash).Replace("-", "").ToLower().TrimStart('0');
        
        // Falls der String nach dem Trimmen komplett leer ist (Wert war 0)
        if (hex.Length == 0) hex = "0";
        
        return isNegative ? "-" + hex : hex;
    }

    private void Say(string message) => Console.WriteLine($"[Session #{Number}] {message}");
}
public sealed class MinecraftAesStream : Stream
{
    private readonly Stream _baseStream;
    private readonly BufferedBlockCipher _cipher;
    public MinecraftAesStream(Stream baseStream, byte[] sharedSecret, bool isEncryptor)
    {
        _baseStream = baseStream;
        _cipher = new BufferedBlockCipher(new CfbBlockCipher(new AesEngine(), 8));
        _cipher.Init(isEncryptor, new ParametersWithIV(new KeyParameter(sharedSecret), sharedSecret));
    }
    public override async NetValueTaskInt ReadAsync(NetMemory buffer, CancellationToken cancellationToken = default)
    {
        int read = await _baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            byte[] raw = buffer.Span.Slice(0, read).ToArray(); byte[] dec = new byte[read];
            _cipher.ProcessBytes(raw, 0, read, dec, 0); dec.AsSpan(0, read).CopyTo(buffer.Span);
        }
        return read;
    }
    public override async NetValueTask WriteAsync(NetReadOnlyMemory buffer, CancellationToken cancellationToken = default)
    {
        byte[] raw = buffer.ToArray(); byte[] enc = new byte[raw.Length];
        _cipher.ProcessBytes(raw, 0, raw.Length, enc, 0);
        await _baseStream.WriteAsync(enc.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _baseStream.Flush();
    public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
internal static class MinecraftStreamUtil
{
    public static string ReadString(Stream s)
    {
        int len = ReadVarInt(s); byte[] b = new byte[len]; int r = 0;
        while (r < len) { int read = s.Read(b, r, len - r); if (read <= 0) throw new EndOfStreamException(); r += read; }
        return System.Text.Encoding.UTF8.GetString(b);
    }
    public static void WriteString(Stream s, string v) { byte[] b = System.Text.Encoding.UTF8.GetBytes(v); WriteVarInt(s, b.Length); s.Write(b, 0, b.Length); }
    public static byte[] ReadByteArray(Stream s)
    {
        int len = ReadVarInt(s); byte[] b = new byte[len]; int r = 0;
        while (r < len) { int read = s.Read(b, r, len - r); if (read <= 0) throw new EndOfStreamException(); r += read; }
        return b;
    }
    public static void WriteByteArray(Stream s, byte[] b) { WriteVarInt(s, b.Length); s.Write(b, 0, b.Length); }
    public static int ReadVarInt(Stream s)
    {
        int val = 0, size = 0, b;
        while (((b = s.ReadByte()) & 0x80) == 0x80) { if (b == -1) throw new EndOfStreamException(); val |= (b & 0x7F) << (size++ * 7); if (size > 5) throw new InvalidDataException(); }
        return val | ((b & 0x7F) << (size * 7));
    }
    public static void WriteVarInt(Stream s, int val) { while ((val & 128) != 0) { s.WriteByte((byte)((val & 127) | 128)); val = (int)((uint)val >> 7); } s.WriteByte((byte)val); }
}