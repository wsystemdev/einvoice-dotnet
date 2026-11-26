using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSystem.EInvoice.Generator;
using WSystem.EInvoice.Support;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>Byte-for-byte parity with the W System reference engine (api.wsystem.dev) and the PHP SDK on 439 fixtures.</summary>
    public class ParityTests
    {
        internal static string Fx(string name) => Path.Combine(System.AppContext.BaseDirectory, "fixtures", name);

        public static IEnumerable<object[]> Cases()
        {
            var inputs = (List<object?>)Json.Decode(File.ReadAllText(Fx("inputs.json")))!;
            foreach (var f in inputs) yield return new object[] { Py.Str(Py.G(f, "name")) };
        }

        private static readonly Dictionary<string, object?> Inputs = ((List<object?>)Json.Decode(File.ReadAllText(Fx("inputs.json")))!)
            .ToDictionary(f => Py.Str(Py.G(f, "name")), f => Py.G(f, "input"));
        private static readonly object? Expected = Json.Decode(File.ReadAllText(Fx("expected.json")));

        [Theory]
        [MemberData(nameof(Cases))]
        public void SameOutputAsReferenceEngine(string name)
        {
            var input = Inputs[name];
            var exp = Py.G(Expected, name);
            BuiltXml r;
            try { r = XmlBuilder.Build(input); }
            catch (InputError e)
            {
                Assert.True(Py.Has(exp, "error"), ".NET raised: " + e.Message);
                Assert.Equal(Py.Str(Py.G(exp, "error")).Replace(", or ksef-fa3", ""), e.Message);
                return;
            }
            Assert.False(Py.Has(exp, "error"), "reference raised: " + Py.Str(Py.G(exp, "error")));
            Assert.Equal(Py.Str(Py.G(exp, "xml")), r.Xml);
            var et = (Dictionary<string, object?>)Py.G(exp, "totals")!;
            Assert.Equal(et.ToDictionary(k => k.Key, k => Py.Str(k.Value)), r.Totals);
        }
    }
}
