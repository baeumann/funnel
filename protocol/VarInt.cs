namespace funnel_.protocol;

public static class StreamUtil
{
    /// <summary>Reads exactly <paramref name="count"/> bytes or throws EndOfStreamException.</summary>
    public static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("connection closed");
            }
            total += read;
        }
    }

    public static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        await ReadExactAsync(stream, one, 0, 1, ct).ConfigureAwait(false);
        return one[0];
    }
}

public static class VarInt
{
    /// <summary>Continues reading a VarInt from a stream whose first byte was already consumed.</summary>
    public static async Task<int> ReadAsync(Stream stream, int firstByte, CancellationToken ct)
    {
        int b = firstByte;
        int value = 0;
        int shift = 0;
        while (true)
        {
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
            shift += 7;
            if (shift >= 35)
            {
                throw new InvalidDataException("VarInt is too big");
            }
            b = await StreamUtil.ReadByteAsync(stream, ct).ConfigureAwait(false);
        }
    }

    public static int Read(byte[] data, ref int offset)
    {
        int value = 0;
        int shift = 0;
        while (true)
        {
            if (offset >= data.Length)
            {
                throw new EndOfStreamException("VarInt runs past the end of the packet");
            }
            byte b = data[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
            shift += 7;
            if (shift >= 35)
            {
                throw new InvalidDataException("VarInt is too big");
            }
        }
    }

    public static byte[] Encode(int value)
    {
        var bytes = new List<byte>(5);
        uint v = (uint)value;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0)
            {
                b |= 0x80;
            }
            bytes.Add(b);
        } while (v != 0);
        return bytes.ToArray();
    }
}
