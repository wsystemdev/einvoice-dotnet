using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using WSystem.EInvoice.Validation;
using WSystem.EInvoice.Validation.Core;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>The pure-.NET XPath engine against answers recorded from the official engine (Saxon).</summary>
    public class CoreEngineTests
    {
        internal static string Dir => Path.Combine(System.AppContext.BaseDirectory, "fixtures", "validator");

        public static IEnumerable<object?[]> Expressions()
        {
            using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "xpath_expected.json")));
            foreach (var p in d.RootElement.EnumerateObject())
                yield return new object?[] { p.Name, p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString() };
        }

        private static XPathEvaluator? _ev;
        private static readonly object Lock = new object();

        [Theory]
        [MemberData(nameof(Expressions))]
        public void SameAnswerAsSaxon(string expr, string? want)
        {
            string got;
            lock (Lock)
            {
                if (_ev == null)
                {
                    var dom = new XmlDocument { PreserveWhitespace = true };
                    dom.Load(Path.Combine(Dir, "xpath_doc.xml"));
                    _ev = new XPathEvaluator(new Tree(dom));
                    _ev.Ns = new Dictionary<string, string> { ["a"] = "urn:a", ["xs"] = "http://www.w3.org/2001/XMLSchema" };
                }
                try
                {
                    var r = _ev.Evaluate(XPathParser.Parse($"string-join(for $i in ({expr}) return string($i), '|')"), _ev.Tree.Root);
                    got = _ev.Str(r[0]);
                }
                catch (XPathError) { got = "ERROR"; }
            }
            Assert.Equal(want, got);
        }

        [Fact]
        public void UnsupportedSyntaxIsRefusedNotGuessed()
        {
            Assert.Throws<XPathUnsupported>(() => XPathParser.Parse("function($x) { $x }"));
        }

        [Fact]
        public void DynamicErrorAbortsWithXsdHint()
        {
            var xml = File.ReadAllText(Path.Combine(Dir, "files/kosit/standard/01.21a-INVOICE_uncefact.xml"));
            var bad = new Regex("<ram:RateApplicablePercent>[^<]*").Replace(xml, "<ram:RateApplicablePercent>XX", 1);
            var e = Assert.Throws<RuleEvaluationError>(() => new Validator().Validate(System.Text.Encoding.UTF8.GetBytes(bad)));
            Assert.IsAssignableFrom<InputError>(e);
            Assert.Equal("dotnet-core", e.Engine);
            Assert.Equal("EN16931", e.Ruleset);
            Assert.NotEmpty(e.XsdErrors);
            Assert.Contains("XX", e.Message);
        }

        [Fact]
        public void ResultNamesTheEngine()
        {
            var r = new Validator().Validate(File.ReadAllBytes(Path.Combine(Dir, "shared/valid/KoSIT_01.01a-INVOICE_ubl.xml")));
            Assert.True(r.Valid, string.Join(", ", r.Errors.Select(x => x.RuleId + ": " + x.Message)));
            Assert.Equal("dotnet-core", r.Engine);
            Assert.Equal("pre-check", r.Conformance);
            Assert.Equal(true, r.Checked["xsd"]);
            Assert.Equal("core-subset", r.Checked["EN16931"]);
            Assert.Equal("core-subset", r.Checked["XRechnung"]);
            Assert.Contains("not the official engine", r.RuleSet);
            Assert.Contains("pre-check, not a conformance check", r.Disclaimer);
            Assert.Empty(r.RulesNotEvaluated!);
            var json = r.ToJson();
            Assert.Contains("\"rule_set\"", json);
        }
    }
}
