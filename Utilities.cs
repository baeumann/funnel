using System.IO.Compression;

namespace funnel_
{
    public class Utilities
    {
        public static byte[] Compress(byte[] uncompressed)
	    {
		    using (var compressedStream = new MemoryStream())
		    {
			    using (var zLibStream = new ZLibStream(compressedStream, CompressionMode.Compress))
			    {
				    zLibStream.Write(uncompressed, 0, uncompressed.Length);
			    }

			    return compressedStream.ToArray();
		    }
	    }
        public static byte[] Decompress(byte[] compressed)
	    {
		    using (var uncompressedStream = new MemoryStream())
		    {
			    using (var zLibStream = new ZLibStream(uncompressedStream, CompressionMode.Decompress))
			    {
				    zLibStream.Write(compressed, 0, compressed.Length);
			    }

			    return uncompressedStream.ToArray();
		    }
	    }
        public static string ByteArrayToString(byte[] byteArray) {
            return BitConverter.ToString(byteArray).Replace("-","");
        }
    }
}