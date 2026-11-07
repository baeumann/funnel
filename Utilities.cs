using System.IO.Compression;

namespace funnel_
{
    public class Utilities
    {
        public static byte[] Compress(byte[] uncompressed)
        {
            using (var compressedStream = new MemoryStream())
            {
                using (var zLibStream = new ZLibStream(compressedStream, CompressionMode.Compress, true))
                {
                    zLibStream.Write(uncompressed, 0, uncompressed.Length);
                }

                return compressedStream.ToArray();
            }
        }

        public static byte[] Decompress(byte[] compressed)
        {
            return Decompress(compressed, 0, -1);
        }

        /// <summary>
        /// Inflates a zlib stream starting at <paramref name="offset"/>.
        /// If <paramref name="expectedLength"/> is not negative the result size is verified.
        /// (The original version wrote into a decompression stream, which throws; reading is the correct direction.)
        /// </summary>
        public static byte[] Decompress(byte[] compressed, int offset, int expectedLength)
        {
            using (var input = new MemoryStream(compressed, offset, compressed.Length - offset))
            using (var zLibStream = new ZLibStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(expectedLength > 0 ? expectedLength : 256))
            {
                zLibStream.CopyTo(output);
                if (expectedLength >= 0 && output.Length != expectedLength)
                {
                    throw new InvalidDataException("decompressed size " + output.Length + " does not match announced size " + expectedLength);
                }
                return output.ToArray();
            }
        }

        public static string ByteArrayToString(byte[] byteArray)
        {
            return BitConverter.ToString(byteArray).Replace("-", "");
        }
    }
}
