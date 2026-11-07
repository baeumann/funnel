using System.Buffers.Binary;
using System.Text;

namespace funnel_.protocol;

/// <summary>Sequential big-endian reader over a packet payload. Throws EndOfStreamException when the data runs out.</summary>
public sealed class PacketReader
{
    private readonly byte[] _data;
    private int _pos;

    public PacketReader(byte[] data, int offset = 0)
    {
        _data = data;
        _pos = offset;
    }

    public int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new EndOfStreamException("packet is shorter than expected");
        }
        var span = new ReadOnlySpan<byte>(_data, _pos, count);
        _pos += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => ReadByte() != 0;
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));
    public int ReadVarInt() => VarInt.Read(_data, ref _pos);

    public string ReadString()
    {
        int length = ReadVarInt();
        return Encoding.UTF8.GetString(Take(length));
    }

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public void Skip(int count)
    {
        Take(count);
    }

    public Guid ReadUuid()
    {
        var b = Take(16);
        return new Guid(
            BinaryPrimitives.ReadInt32BigEndian(b.Slice(0, 4)),
            BinaryPrimitives.ReadInt16BigEndian(b.Slice(4, 2)),
            BinaryPrimitives.ReadInt16BigEndian(b.Slice(6, 2)),
            b.Slice(8, 8).ToArray());
    }
}
