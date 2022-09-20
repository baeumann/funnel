namespace funnel_
{
    public class InjectorStream : Stream
    {
        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => _stream.Length;

        public override long Position { get => _stream.Position; set => _stream.Position = value; }

        private Stream _stream;
        public InjectorStream(Stream stream) {
            _stream = stream;
        }

        public override void Flush()
        {
            _stream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            //do injection stuff
            var readResult = _stream.Read(buffer, offset, count);
            Console.WriteLine(">> READ" + Utilities.ByteArrayToString(buffer));

            return readResult;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _stream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
            Console.WriteLine(">> WRITE" + Utilities.ByteArrayToString(buffer));
        }
    }
}