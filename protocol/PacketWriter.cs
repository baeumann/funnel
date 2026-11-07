using System.Buffers.Binary;
using System.Text;

namespace funnel_.protocol;

/// <summary>Builds a packet payload (without length / id) using Minecraft's big-endian encodings.</summary>
public sealed class PacketWriter
{
    private readonly MemoryStream _ms = new MemoryStream();

    public PacketWriter WriteByte(byte value)
    {
        _ms.WriteByte(value);
        return this;
    }

    public PacketWriter WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public PacketWriter WriteUShort(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public PacketWriter WriteInt(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public PacketWriter WriteLong(long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public PacketWriter WriteFloat(float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public PacketWriter WriteDouble(double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public PacketWriter WriteVarInt(int value)
    {
        var bytes = VarInt.Encode(value);
        _ms.Write(bytes, 0, bytes.Length);
        return this;
    }

    public PacketWriter WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(bytes.Length);
        _ms.Write(bytes, 0, bytes.Length);
        return this;
    }

    public PacketWriter WriteBytes(byte[] value)
    {
        _ms.Write(value, 0, value.Length);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();
}
