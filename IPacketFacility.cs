namespace funnel_
{
    public interface IPacketFacility
    {
        void Add(byte[] buffer);
        bool HasNewPacket();
        Packet PeekPacket();
        Packet GetPacket();
        byte[] GetEncodedPacket();
        byte[] Encode(Packet packet);
        Packet Decode(byte[] buffer);
    }
}