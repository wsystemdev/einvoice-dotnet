using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using WSystem.EInvoice.Support;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>Untrusted input: decompression bombs, malformed JSON, nulls (QA findings DN-1, DN-4, DN-5).</summary>
    public class HardeningTests
    {
        private static string Bomb => Path.Combine(AppContext.BaseDirectory, "fixtures", "bomb", "bomb-1gib.pdf");
        private const string Limit = "decompressed data exceeds the limit of 75 MB";

        private static long Allocated() =>
#if NETFRAMEWORK
            GC.GetTotalMemory(false);
#else
            GC.GetTotalAllocatedBytes(true);
#endif

        [Fact]
        public void OneGiBBombIsRefusedWithoutInflatingIt()
        {
            // tools/ci/make_bomb.py bomb-1gib.pdf 1024: 1 MB PDF whose factur-x.xml inflates to 1 GiB
            var data = File.ReadAllBytes(Bomb);
            Assert.True(data.Length < 1_100_000);
            var before = Allocated();
            var e = Assert.Throws<InputError>(() => Parser.Parse(data));
            Assert.Contains(Limit, e.Message);
            Assert.True(Allocated() - before < 700L * 1024 * 1024, "allocated " + (Allocated() - before) / 1048576 + " MB");
            Assert.Contains(Limit, Assert.ThrowsAny<InputError>(() => new Validation.Validator().Validate(data)).Message);
            Assert.Contains(Limit, Assert.ThrowsAny<InputError>(() => new EInvoice().Validate(data)).Message);
        }

        private static byte[] Zlib(int zeros)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0xDA);
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            {
                var chunk = new byte[1 << 20];
                for (int left = zeros; left > 0; left -= chunk.Length) ds.Write(chunk, 0, Math.Min(left, chunk.Length));
            }
            return ms.ToArray();
        }

        /// <summary>Several object streams that are each under the limit but together over it.</summary>
        [Fact]
        public void LimitCountsAllStreamsOfOnePdf()
        {
            var objs = new List<byte[]>
            {
                Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
                Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [] /Count 0 >>"),
            };
            var pdf = new MemoryStream();
            void W(string s) { var b = Encoding.ASCII.GetBytes(s); pdf.Write(b, 0, b.Length); }
            W("%PDF-1.7\n");
            var offs = new List<long>();
            for (int i = 0; i < objs.Count; i++) { offs.Add(pdf.Length); W((i + 1) + " 0 obj\n"); pdf.Write(objs[i], 0, objs[i].Length); W("\nendobj\n"); }
            var z = Zlib(30_000_000);
            for (int k = 0; k < 3; k++)
            {
                offs.Add(pdf.Length);
                W((objs.Count + k + 1) + " 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode /Length " + z.Length + " >>\nstream\n");
                pdf.Write(z, 0, z.Length);
                W("\nendstream\nendobj\n");
            }
            var x = pdf.Length;
            W("xref\n0 " + (offs.Count + 1) + "\n0000000000 65535 f \n" + string.Concat(offs.Select(o => o.ToString("D10") + " 00000 n \n")));
            W("trailer\n<< /Size " + (offs.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + x + "\n%%EOF\n");
            var e = Assert.ThrowsAny<InputError>(() => Parser.Parse(pdf.ToArray()));
            Assert.Contains(Limit, e.Message);
        }

        [Theory]
        [InlineData("not json", "invalid JSON")]
        [InlineData("{\"a\":", "invalid JSON")]
        [InlineData("[]", "must be a JSON object")]
        [InlineData("null", "must be a JSON object")]
        [InlineData("42", "must be a JSON object")]
        public void BadJsonIsAnInputError(string json, string msg)
        {
            var e = Assert.Throws<InputError>(() => new EInvoice().Create(json));
            Assert.Contains(msg, e.Message);
            Assert.Contains(msg, Assert.Throws<InputError>(() => EInvoice.Xml(json)).Message);
        }

        [Fact]
        public void NullsAreArgumentNullExceptions()
        {
            var ei = new EInvoice();
            Assert.Throws<ArgumentNullException>(() => ei.Create((Invoice)null!));
            Assert.Throws<ArgumentNullException>(() => ei.Create((string)null!));
            Assert.Throws<ArgumentNullException>(() => ei.Create((IDictionary<string, object?>)null!));
            Assert.Throws<ArgumentNullException>(() => ei.Validate((byte[])null!));
            Assert.Throws<ArgumentNullException>(() => ei.Validate((string)null!));
            Assert.Throws<ArgumentNullException>(() => Parser.Parse(null!));
            Assert.Throws<ArgumentNullException>(() => new Validation.Validator().Validate(null!));
        }

        [Fact]
        public void NoValidationDisclaimerDoesNotClaimAnyCheck()
        {
            var first = ((List<object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("inputs.json")))!)[0];
            var r = new EInvoice(new EInvoiceOptions { NoValidation = true }).Create((IDictionary<string, object?>)Py.G(first, "input")!);
            Assert.Null(r.Valid);
            Assert.StartsWith("Not validated", r.Disclaimer);
            Assert.DoesNotContain("Technical check against", r.Disclaimer);
        }
    }
}
