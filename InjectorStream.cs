using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace funnel_
{
    public class InjectorStream : Stream
    {
        private readonly Stream _stream;

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }

        public InjectorStream(Stream stream) 
        {
            _stream = stream;
        }

        public override void Flush()
        {
            _stream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var readResult = _stream.Read(buffer, offset, count);
            if (readResult > 0)
            {
                // Erstellt ein exaktes temporäres Array für die gelesenen Bytes zum Loggen
                byte[] loggedBytes = new byte[readResult];
                System.Buffer.BlockCopy(buffer, offset, loggedBytes, 0, readResult);
                System.Console.WriteLine(">> READ (Sync): " + Utilities.ByteArrayToString(loggedBytes));
            }
            return readResult;
        }

        // =========================================================================
        // ERFORDERLICH: Asynchrones Lesen für den Minecraft-Protokoll-Stream abfangen
        // =========================================================================
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int readResult = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (readResult > 0)
            {
                byte[] loggedBytes = buffer.Span.Slice(0, readResult).ToArray();
                System.Console.WriteLine(">> READ (Async): " + Utilities.ByteArrayToString(loggedBytes));
            }
            return readResult;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
            byte[] loggedBytes = new byte[count];
            System.Buffer.BlockCopy(buffer, offset, loggedBytes, 0, count);
            System.Console.WriteLine(">> WRITE (Sync): " + Utilities.ByteArrayToString(loggedBytes));
        }

        // =========================================================================
        // ERFORDERLICH: Asynchrones Schreiben für den Minecraft-Protokoll-Stream abfangen
        // =========================================================================
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            System.Console.WriteLine(">> WRITE (Async): " + Utilities.ByteArrayToString(buffer.ToArray()));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _stream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _stream.SetLength(value);
        }
    }
}
