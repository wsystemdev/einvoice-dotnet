using System.IO;
using System.IO.Compression;
using System.Text;

namespace WSystem.EInvoice.Support
{
    /// <summary>Byte helpers: bytes as a Latin-1 string (1 char = 1 byte) for regex work on PDFs, zlib in/out.</summary>
    internal static class Bytes
    {
        public static string Latin1(byte[] b)
        {
            var c = new char[b.Length];
            for (int i = 0; i < b.Length; i++) c[i] = (char)b[i];
            return new string(c);
        }

        public static byte[] FromLatin1(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
            return b;
        }

        /// <summary>zlib (RFC 1950) or raw deflate data -> bytes; null when corrupt.</summary>
        public static byte[]? Inflate(byte[] data)
        {
            foreach (var skip in new[] { 2, 0 })
            {
                if (data.Length <= skip) continue;
                try
                {
                    using var ms = new MemoryStream(data, skip, data.Length - skip);
                    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                    using var outp = new MemoryStream();
                    ds.CopyTo(outp);
                    return outp.ToArray();
                }
                catch (InvalidDataException) { }
            }
            return null;
        }

        /// <summary>zlib-compress (header 78 9C + deflate + Adler-32), like PHP gzcompress.</summary>
        public static byte[] Deflate(byte[] data)
        {
            using var outp = new MemoryStream();
            outp.WriteByte(0x78); outp.WriteByte(0x9C);
            using (var ds = new DeflateStream(outp, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            outp.WriteByte((byte)(adler >> 24)); outp.WriteByte((byte)(adler >> 16)); outp.WriteByte((byte)(adler >> 8)); outp.WriteByte((byte)adler);
            return outp.ToArray();
        }

        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        public static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);
    }
}
