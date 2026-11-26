using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Generator;
using WSystem.EInvoice.Pdf;
using WSystem.EInvoice.Support;
using WSystem.EInvoice.Validation;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>
    /// PDF/A-3b hybrids: same objects and page content as the PHP SDK's PDF for every PDF-format fixture (compared after
    /// inflating streams, since zlib builds may compress differently), and every PDF round-trips through the validator.
    /// </summary>
    public class PdfTests
    {
        internal static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1764000000);
        private static readonly Dictionary<string, object?> Inputs = ((List<object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("inputs.json")))!)
            .ToDictionary(f => Py.Str(Py.G(f, "name")), f => Py.G(f, "input"));
        private static readonly object? Golden = Json.Decode(File.ReadAllText(ParityTests.Fx("pdf_golden.json")));

        public static IEnumerable<object[]> Cases() => ((Dictionary<string, object?>)Json.Decode(File.ReadAllText(ParityTests.Fx("pdf_golden.json")))!).Keys.Select(k => new object[] { k });

        internal static (byte[] Pdf, bool Replaced, BuiltXml B) Build(string name)
        {
            var i = Inputs[name];
            var b = XmlBuilder.Build(i);
            var (pdf, rep) = HybridPdf.Build(i, b.Model, b.Xml, b.Format, b.Pdf!.FileName!, b.Pdf.Level!, T);
            return (pdf, rep, b);
        }

        /// <summary>PDF with every Flate stream inflated, /Length values, the xref table, startxref and /ID removed.</summary>
        internal static string Normalize(byte[] pdf)
        {
            var s = Bytes.Latin1(pdf);
            var sb = new StringBuilder();
            int pos = 0;
            var re = new Regex(@"<<(?<d>(?:(?!stream\n).)*?)>>\nstream\n", RegexOptions.Singleline);
            while (true)
            {
                var m = re.Match(s, pos);
                if (!m.Success) break;
                var start = m.Index + m.Length;
                var len = int.Parse(Regex.Match(m.Groups["d"].Value, @"/Length (\d+)").Groups[1].Value);
                var data = s.Substring(start, len);
                var dict = Regex.Replace(m.Groups["d"].Value, @"/Length \d+", "/Length _");
                sb.Append(s, pos, m.Index - pos).Append("<<").Append(dict).Append(">>\nstream\n");
                if (dict.Contains("/FlateDecode")) sb.Append(Bytes.Latin1(Bytes.Inflate(Bytes.FromLatin1(data)) ?? Array.Empty<byte>()));
                else sb.Append(data);
                pos = start + len;
            }
            sb.Append(s, pos, s.Length - pos);
            var t = sb.ToString();
            t = Regex.Replace(t, @"<</Length \d+\n", "<</Length _\n");
            t = Regex.Replace(t, @"\nxref\n.*", "", RegexOptions.Singleline);
            return t;
        }

        internal static string Sha(string s)
        {
            using var h = SHA256.Create();
            return string.Concat(h.ComputeHash(Bytes.FromLatin1(s)).Select(x => x.ToString("x2")));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void SamePdfAsPhpSdk(string name)
        {
            var (pdf, rep, _) = Build(name);
            var g = Py.G(Golden, name);
            var refDir = Environment.GetEnvironmentVariable("WSYSTEM_PDF_REF_DIR");
            if (!string.IsNullOrEmpty(refDir))
            {
                var want = Normalize(File.ReadAllBytes(Path.Combine(refDir, name + ".pdf")));
                Assert.Equal(want.Replace("for PHP", "for .NET"), Normalize(pdf));
            }
            Assert.Equal(Py.Str(Py.G(g, "normalized_sha256")), Sha(Normalize(pdf)));
            Assert.Equal((bool)Py.G(g, "replaced")!, rep);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void PdfRoundTripsThroughValidator(string name)
        {
            var (pdf, _, b) = Build(name);
            var r = new Validator().Validate(pdf);
            var x = new Validator().Validate(Bytes.Utf8.GetBytes(b.Xml));
            Assert.Equal("pdf", r.Container);
            Assert.Equal(b.Pdf!.FileName, r.EmbeddedFile);
            Assert.Equal(x.Valid, r.Valid);
            Assert.Equal(x.Errors.Select(e => e.RuleId), r.Errors.Select(e => e.RuleId));
            Assert.Equal(b.Xml, Bytes.Utf8.GetString(AttachmentReader.Read(pdf)[b.Pdf.FileName!]));
        }

        [Fact]
        public void PdfA3Markers()
        {
            var (pdf, _, _) = Build("sample-zugferd");
            var s = Bytes.Latin1(pdf);
            Assert.StartsWith("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n", s);
            Assert.Contains("<pdfaid:part>3</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>", s);
            Assert.Contains("/AFRelationship /Alternative", s);
            Assert.Contains("/OutputIntents [", s);
            Assert.Contains("W System e-invoice SDK for .NET", s);
            Assert.DoesNotContain("for PHP", s);
            Assert.EndsWith("%%EOF\n", s);
        }
    }
}
