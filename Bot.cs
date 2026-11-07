using System.Text.Json;
using funnel_.protocol;

namespace funnel_.core;

/// <summary>
/// Ready-made packets a bot can send. Layouts are version dependent, so each helper states the protocol
/// range it was written for and throws NotSupportedException outside it. Anything else can be sent with
/// <see cref="Session.SendRawAsync"/>.
/// </summary>
public sealed class Bot
{
    private readonly Session _session;

    public Bot(Session session)
    {
        _session = session;
    }

    /// <summary>Chat message as the player (1.19.3+). Sent unsigned, so it is only accepted by servers that do not enforce chat signing.</summary>
    public Task SayAsync(string message)
    {
        if (_session.ProtocolVersion < 761)
        {
            throw new NotSupportedException("chat injection is written for 1.19.3 and later (protocol 761+)");
        }

        var payload = new PacketWriter()
            .WriteString(message)
            .WriteLong(NowMillis())
            .WriteLong(Random.Shared.NextInt64())
            .WriteBool(false)           // no signature
            .WriteVarInt(0)             // last-seen message count
            .WriteBytes(new byte[3])    // acknowledged bit set (20 bits)
            .ToArray();
        return _session.SendAsync(Direction.Serverbound, payload, "chat");
    }

    /// <summary>Runs a command as the player (without the leading slash).</summary>
    public Task CommandAsync(string command)
    {
        if (command.StartsWith("/", StringComparison.Ordinal))
        {
            command = command.Substring(1);
        }

        if (_session.ProtocolVersion >= 766)
        {
            // 1.20.5+: plain unsigned command packet
            var payload = new PacketWriter().WriteString(command).ToArray();
            return _session.SendAsync(Direction.Serverbound, payload, "chat_command");
        }
        if (_session.ProtocolVersion >= 761)
        {
            // 1.19.3 - 1.20.4: the only command packet carries signing fields
            var payload = new PacketWriter()
                .WriteString(command)
                .WriteLong(NowMillis())
                .WriteLong(Random.Shared.NextInt64())
                .WriteVarInt(0)             // no argument signatures
                .WriteVarInt(0)             // last-seen message count
                .WriteBytes(new byte[3])    // acknowledged bit set
                .ToArray();
            return _session.SendAsync(Direction.Serverbound, payload, "chat_command");
        }
        throw new NotSupportedException("command injection is written for 1.19.3 and later (protocol 761+)");
    }

    /// <summary>Respawn after dying.</summary>
    public Task RespawnAsync()
    {
        var payload = new PacketWriter().WriteVarInt(0).ToArray();
        return _session.SendAsync(Direction.Serverbound, payload, "client_command");
    }

    /// <summary>Swing the main hand.</summary>
    public Task SwingAsync()
    {
        var payload = new PacketWriter().WriteVarInt(0).ToArray();
        return _session.SendAsync(Direction.Serverbound, payload, "swing", "animate");
    }

    /// <summary>
    /// Reports a position to the server. The real client keeps sending its own position too, so the server
    /// will see both; this is mainly useful for a client that is standing still.
    /// </summary>
    public Task MoveToAsync(double x, double y, double z, bool onGround = true)
    {
        var payload = new PacketWriter()
            .WriteDouble(x).WriteDouble(y).WriteDouble(z)
            .WriteByte(onGround ? (byte)1 : (byte)0)
            .ToArray();
        return _session.SendAsync(Direction.Serverbound, payload, "move_player_pos");
    }

    /// <summary>Shows a message in the player's own chat window. It never goes to the server.</summary>
    public Task EchoAsync(string message)
    {
        int protocol = _session.ProtocolVersion;
        if (protocol < 760)
        {
            throw new NotSupportedException("echo is written for 1.19.1 and later (protocol 760+)");
        }

        var writer = new PacketWriter();
        if (protocol >= 765)
        {
            // 1.20.3+: nameless network NBT, a plain string tag is a valid text component
            var text = System.Text.Encoding.UTF8.GetBytes(message);
            writer.WriteByte(8).WriteUShort((ushort)text.Length).WriteBytes(text);
        }
        else
        {
            writer.WriteString(JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = message }));
        }
        writer.WriteBool(false); // not the action bar

        return _session.SendAsync(Direction.Clientbound, writer.ToArray(), "system_chat");
    }

    private static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
