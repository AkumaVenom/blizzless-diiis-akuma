using System;
using System.IO;
using System.IO.Compression;

namespace DiIiS_NA.REST.IO.Zlib
{
    public static partial class ZLib
    {
        /// <summary>
        /// Compresses payload using RFC1950 (zlib wrapper) via .NET's native-backed ZLibStream.
        /// Output includes zlib header + Adler32, matching Diablo III packet expectations.
        /// </summary>
        public static byte[] Compress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) return Array.Empty<byte>();

            using var ms = new MemoryStream(capacity: Math.Min(data.Length, 64 * 1024));
            // ZLibStream produces the correct zlib-wrapped DEFLATE stream with Adler32 automatically.
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Decompresses RFC1950 (zlib wrapper) payload via .NET's native-backed ZLibStream.
        /// Reads exactly <paramref name="unpackedSize"/> bytes for performance and correctness.
        /// </summary>
        public static byte[] Decompress(byte[] data, uint unpackedSize)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            int expectedSize = checked((int)unpackedSize);
            if (expectedSize == 0) return Array.Empty<byte>();

            var output = new byte[expectedSize];

            using var input = new MemoryStream(data, writable: false);
            using var z = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);

            int offset = 0;
            while (offset < expectedSize)
            {
                int read = z.Read(output, offset, expectedSize - offset);
                if (read <= 0)
                {
                    // Premature end of stream; input is truncated or unpackedSize is wrong.
                    throw new InvalidDataException($"Unexpected end of zlib stream (read {offset} of {expectedSize} bytes).");
                }
                offset += read;
            }

            return output;
        }
    }
}
