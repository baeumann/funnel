namespace funnel_.protocol;

public enum ConnectionState
{
    Handshake,
    Status,
    Login,
    Configuration,
    Play
}

public enum Direction
{
    Clientbound,
    Serverbound
}

/// <summary>Shared between both directions of one connection. -1 means compression is off.</summary>
public sealed class CompressionState
{
    public volatile int Threshold = -1;
}

/// <summary>
/// One packet as it appeared on the wire. <see cref="Raw"/> is the complete frame (length prefix included)
/// exactly as received, so it can be forwarded without re-encoding. Id is -1 if the frame could not be decoded.
/// </summary>
public sealed record Frame(int Id, byte[] Payload, byte[] Raw);

public static class PacketFramer
{
    private const int MaxFrameLength = 1 << 25; // 32 MiB, far above anything vanilla sends

    /// <summary>
    /// Reads one frame. The first byte of the length prefix is passed in because the caller has already
    /// read it (to decide between framed and raw mode). The compression threshold is looked up only after
    /// the frame has arrived, so a Set Compression packet handled by the other direction is always respected.
    /// </summary>
    public static async Task<Frame> ReadAsync(Stream stream, int firstByte, CompressionState compression, CancellationToken ct)
    {
        int length = await VarInt.ReadAsync(stream, firstByte, ct).ConfigureAwait(false);
        if (length < 1 || length > MaxFrameLength)
        {
            throw new InvalidDataException("invalid frame length " + length);
        }

        var body = new byte[length];
        await StreamUtil.ReadExactAsync(stream, body, 0, length, ct).ConfigureAwait(false);

        var prefix = VarInt.Encode(length);
        var raw = new byte[prefix.Length + length];
        prefix.CopyTo(raw, 0);
        body.CopyTo(raw, prefix.Length);

        try
        {
            byte[] data;
            if (compression.Threshold >= 0)
            {
                int offset = 0;
                int dataLength = VarInt.Read(body, ref offset);
                data = dataLength == 0
                    ? body[offset..]
                    : Utilities.Decompress(body, offset, dataLength);
            }
            else
            {
                data = body;
            }

            int pos = 0;
            int id = VarInt.Read(data, ref pos);
            return new Frame(id, data[pos..], raw);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException)
        {
            // Framing is still in sync (the length was honoured), so forward the bytes untouched.
            return new Frame(-1, Array.Empty<byte>(), raw);
        }
    }

    /// <summary>Builds a complete frame for an injected packet, honouring the connection's compression threshold.</summary>
    public static byte[] Encode(int id, byte[] payload, int threshold)
    {
        var idBytes = VarInt.Encode(id);
        var inner = Concat(idBytes, payload);

        byte[] body;
        if (threshold < 0)
        {
            body = inner;
        }
        else if (inner.Length < threshold)
        {
            body = Concat(VarInt.Encode(0), inner);
        }
        else
        {
            body = Concat(VarInt.Encode(inner.Length), Utilities.Compress(inner));
        }

        return Concat(VarInt.Encode(body.Length), body);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}
