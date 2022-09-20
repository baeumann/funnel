using System.Collections;
using System.Security.Cryptography;
using System.Numerics;
using System.Text;
using funnel_.core;

namespace funnel_
{
    public class PacketFacility : IPacketFacility
    {
        private Queue<Packet> _packetQueue;
        private ArrayList _buffer;
        private Encryption _encryption;
        public PacketFacility() {
            _buffer = new ArrayList();
            _packetQueue = new Queue<Packet>();
            _encryption = new Encryption();
        }
        public void Add(byte[] buffer)
        {
            Console.WriteLine(">> " + Utilities.ByteArrayToString(buffer));
            _buffer.AddRange(buffer);
        }

        public Packet Decode(byte[] buffer)
        {
            throw new NotImplementedException();
        }

        public byte[] Encode(Packet packet)
        {
            throw new NotImplementedException();
        }

        public byte[] GetEncodedPacket()
        {
            return Encode(GetPacket());
        }

        public Packet GetPacket()
        {
            return _packetQueue.First<Packet>();
        }

        public bool HasNewPacket()
        {
            throw new NotImplementedException();
        }

        public Packet PeekPacket()
        {
            if(_packetQueue == null || _packetQueue.Count == 0) {
                return null;
            }
            return _packetQueue.Peek();
        }

    }
}