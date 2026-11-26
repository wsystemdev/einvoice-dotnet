using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSystem.EInvoice.Support;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>Parser.Parse gives exactly the W System engine's parse output.</summary>
    public class ParserTests
    {
        private static string Dir => CoreEngineTests.Dir;
        private static readonly Dictionary<string, object?> Golden = (Dictionary<string, object?>)Json.Decode(File.ReadAllText(Path.Combine(Dir, "parse_golden.json")))!;
        public static IEnumerable<object[]> Files() => Golden.Keys.Select(k => new object[] { k });

        [Theory]
        [MemberData(nameof(Files))]
        public void SameAsReferenceEngine(string file)
        {
            var want = Golden[file];
            var data = File.ReadAllBytes(Path.Combine(Dir, file));
            if (Py.Has(want, "input_error"))
            {
                Assert.ThrowsAny<InputError>(() => Parser.Parse(data));
                return;
            }
            if (file.EndsWith("04.05a-INVOICE_uncefact.xml"))
            {
                // Known reference-engine bug, not copied: it returns an XML comment as the delivery date.
                var dl = (Dictionary<string, object?>)Py.G(Py.G(want, "invoice"), "delivery")!;
                Assert.Equal("Tag der Leistungserbringung", dl["date"]);
                dl["date"] = "2020-01-02";
            }
            Assert.Equal(Json.Encode(want), Json.Encode(Parser.Parse(data)));
        }
    }
}
