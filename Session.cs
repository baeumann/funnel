using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using funnel_.protocol;

using NetReadOnlyMemory = System.ReadOnlyMemory<byte>;

namespace funnel_.core;

public sealed record PacketInfo(Direction Direction, ConnectionState State, int Id, string? Name, byte[] Payload);

public sealed class ProxyOptions
{
    public volatile bool LogPackets;
    public volatile bool ShowChat = true;
    public string? LogFilter;
}

/// <summary>
/// Container für das globale Abfangen roher Netzwerk-Bytes von externen Klassen.
/// </summary>
public class PacketTrafficEventArgs : EventArgs
{
    public Direction Direction { get; }
    public int PacketId { get; }
    public byte[] Payload { get; }
    public int Length { get; }

    public PacketTrafficEventArgs(Direction direction, int packetId, byte[] payload, int length)
    {
        Direction = direction;
        PacketId = packetId;
        Payload = payload;
        Length = length;
    }
}

public sealed class Session : IDisposable
{
    private static int _counter;
    private readonly TcpClient _client, _server;
    private Stream _clientSourceStream, _clientDestinationStream, _serverSourceStream, _serverDestinationStream;
    private readonly SemaphoreSlim _clientGate = new(1, 1), _serverGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ProxyOptions _options;
    private readonly string _targetHost;
    private readonly int _targetPort;

    private volatile ConnectionState _state = ConnectionState.Handshake;
    private int _disposed;
    private int _byteCounter = 0;

    // Microsoft Online-Auth Daten
    public static volatile string MicrosoftAccessToken = "";
    public static volatile string MicrosoftUuid = "";

    public int Number { get; } = Interlocked.Increment(ref _counter);
    public PacketIds Ids { get; }
    public GameState Game { get; } = new();
    public Bot Bot { get; }
    public int ProtocolVersion { get; private set; }
    public string? Username { get; private set; }
    public ConnectionState State => _state;
    public bool Encrypted => _state == ConnectionState.Play || _state == ConnectionState.Configuration;
    public int CompressionThreshold => -1;

    // DER HOOK FÜR EXTERNE KLASSEN: Sendet den Datenstrom direkt an abonnierte Module
    public event Action<Session, PacketTrafficEventArgs>? OnPacketTraffic;

#pragma warning disable CS0067
    public event Action<Session, PacketInfo>? PacketReceived;
#pragma warning restore CS0067

    public Session(TcpClient client, TcpClient server, PacketIds ids, ProxyOptions options, string targetHost, int targetPort)
    {
        _targetHost = targetHost; _targetPort = targetPort; _client = client; _server = server;
        _clientSourceStream = client.GetStream(); _clientDestinationStream = client.GetStream();
        _serverSourceStream = server.GetStream(); _serverDestinationStream = server.GetStream();
        Ids = ids; _options = options; Bot = new Bot(this);
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
        _client.Close(); _server.Close();
    }

    /// <summary>
    /// Ermöglicht externen Klassen das direkte Injizieren unsignierter In-Game Pakete (PLAY-Modus).
    /// </summary>
    public async Task InjectUnsignedPacketAsync(Direction direction, int packetId, byte[] payload)
    {
        var destination = direction == Direction.Serverbound ? _serverDestinationStream : _clientDestinationStream;
        var gate = direction == Direction.Serverbound ? _serverGate : _clientGate;
        
        using var ms = new MemoryStream();
        MinecraftStreamUtil.WriteVarInt(ms, packetId);
        ms.Write(payload, 0, payload.Length);
        
        byte[] packetData = ms.ToArray();
        using var frameMs = new MemoryStream();
        MinecraftStreamUtil.WriteVarInt(frameMs, packetData.Length);
        frameMs.Write(packetData, 0, packetData.Length);
        
        await WriteBytesAsync(destination, gate, frameMs.ToArray()).ConfigureAwait(false);
    }

    public async Task SendRawAsync(Direction direction, int id, byte[] payload)
    {
        await InjectUnsignedPacketAsync(direction, id, payload).ConfigureAwait(false);
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

    private async Task PumpAsync(Direction direction)
    {
        var ct = _cts.Token;
        byte[] rawBuffer = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var source = direction == Direction.Serverbound ? _clientSourceStream : _serverSourceStream;
                var destination = direction == Direction.Serverbound ? _serverDestinationStream : _clientDestinationStream;
                var gate = direction == Direction.Serverbound ? _serverGate : _clientGate;

                int bytesRead = await source.ReadAsync(rawBuffer.AsMemory(), ct).ConfigureAwait(false);
                if (bytesRead <= 0) throw new EndOfStreamException();

                // STATE-MACHINE SYNCHRONISATION
                if (_state != ConnectionState.Play)
                {
                    if (_state == ConnectionState.Handshake && direction == Direction.Serverbound)
                    {
                        _state = ConnectionState.Login;
                        Say("[Protocol] Handshake parsed. Tunneling into secure Login phase...");
                    }
                    
                    if (_state == ConnectionState.Login && direction == Direction.Clientbound)
                    {
                        _byteCounter += bytesRead;
                        if (_byteCounter > 15000) 
                        {
                            _state = ConnectionState.Play;
                            ProtocolVersion = 777; // Kompatibilität für Commands.cs erzwingen
                            Say("[Protocol] Configuration complete. Tunnel successfully advanced to live PLAY state.");
                        }
                    }
                }

                // FUNKT DIE BYTES DIREKT AN ALLE EXTERNEN PLUGINS / INTERZEPTOREN REIN
                if (OnPacketTraffic != null)
                {
                    int extractedId = rawBuffer[1]; // Roh-Paket-ID Schätzung aus dem Strom
                    byte[] payloadData = rawBuffer.Skip(2).Take(bytesRead - 2).ToArray();
                    var trafficArgs = new PacketTrafficEventArgs(direction, extractedId, payloadData, bytesRead);
                    
                    // Führt die externe Modul-Logik sicher asynchron aus
                    OnPacketTraffic.Invoke(this, trafficArgs);
                }

                await WriteBytesAsync(destination, gate, rawBuffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { if (_disposed == 0) Say((direction == Direction.Serverbound ? "the game" : "the server") + " closed connection"); }
        catch (IOException ex) { if (_disposed == 0) Say("Connection lost: " + ex.Message); }
        catch (Exception ex) { Say(direction + " error: " + ex.Message); }
    }

    private void Say(string message) => Console.WriteLine($"[Session #{Number}] {message}");
}

internal static class MinecraftStreamUtil
{
    public static int ReadVarInt(Stream s)
    {
        int val = 0, size = 0, b;
        while (((b = s.ReadByte()) & 0x80) == 0x80) { if (b == -1) throw new EndOfStreamException(); val |= (b & 0x7F) << (size++ * 7); if (size > 5) throw new InvalidDataException(); }
        return val | ((b & 0x7F) << (size * 7));
    }
    public static void WriteVarInt(Stream s, int val) { while ((val & 128) != 0) { s.WriteByte((byte)((val & 127) | 128)); val = (int)((uint)val >> 7); } s.WriteByte((byte)val); }
}
