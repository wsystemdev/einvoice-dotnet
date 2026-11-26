using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSystem.EInvoice.Support;
using WSystem.EInvoice.Validation;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>
    /// The shared validator corpus (CEN, KoSIT, Mustang, W System files) with golden results from the W System reference
    /// engine: verdict, rule ids, severities, rule sets, messages, locations and business terms must match. XML Schema
    /// messages come from a different schema processor, so for XSD only presence is compared.
    /// </summary>
    public class GoldenCorpusTests
    {
        private static string Dir => CoreEngineTests.Dir;
        private static readonly Dictionary<string, object?> Cases = (Dictionary<string, object?>)Py.G(Json.Decode(File.ReadAllText(Path.Combine(Dir, "golden.json"))), "cases")!;
        private static readonly Validator V = new Validator();

        public static IEnumerable<object[]> Corpus() => Cases.Keys.Select(k => new object[] { k });

        private static List<string> Sig(IEnumerable<object?> entries) => entries.Where(i => Py.Str(Py.G(i, "rule_id")) != "XSD")
            .Select(i => string.Join(" | ", Py.Str(Py.G(i, "rule_id")), Py.Str(Py.G(i, "severity")), Py.Str(Py.G(i, "ruleset")),
                Py.G(i, "location") == null ? "" : Py.Str(Py.G(i, "location")), Py.Str(Py.G(i, "message")))).ToList();
        private static List<string> Sig(IEnumerable<ValidationIssue> entries) => entries.Where(i => i.RuleId != "XSD")
            .Select(i => string.Join(" | ", i.RuleId, i.Severity, i.Ruleset, i.Location ?? "", i.Message)).ToList();

        [Theory]
        [MemberData(nameof(Corpus))]
        public void SameResultAsReferenceEngine(string key)
        {
            var c = Cases[key];
            var bytes = File.ReadAllBytes(Path.Combine(Dir, "files", Py.Str(Py.G(c, "file"))));
            ValidationResult r;
            try { r = V.Validate(bytes, Py.Str(Py.G(c, "rules"))); }
            catch (InputError e)
            {
                Assert.True(Py.G(c, "input_error") != null, "unexpected input error: " + e.Message);
                return;
            }
            Assert.True(Py.G(c, "input_error") == null, "reference raised: " + Py.Str(Py.G(c, "input_error")));
            var ce = Py.AsList(Py.G(c, "errors")); var cw = Py.AsList(Py.G(c, "warnings"));
            Assert.Equal(Sig(ce), Sig(r.Errors));
            Assert.Equal(Sig(cw), Sig(r.Warnings));
            Assert.Equal(ce.Any(i => Py.Str(Py.G(i, "rule_id")) == "XSD"), r.Errors.Any(i => i.RuleId == "XSD"));
            Assert.Equal((bool)Py.G(c, "valid")!, r.Valid);
            Assert.Equal(Py.Str(Py.G(c, "syntax")), r.Syntax);
            Assert.Equal(Py.Str(Py.G(c, "document_type")), r.DocumentType);
            Assert.Equal(Py.Str(Py.G(c, "customization_id")), r.CustomizationId);
            Assert.Equal(Py.Str(Py.G(c, "profile")), r.Profile);
            Assert.Equal(Py.Str(Py.G(c, "container")), r.Container);
            Assert.Equal(Py.AsList(Py.G(c, "notes")).Select(Py.Str).ToList(), r.Notes);
            Assert.Equal(Py.G(c, "embedded_file") as string, r.EmbeddedFile);
            var nonXsd = r.Errors.Where(i => i.RuleId != "XSD").ToList();
            foreach (var (e, i) in ce.Where(i => Py.Str(Py.G(i, "rule_id")) != "XSD").Select((e, i) => (e, i)))
                Assert.Equal(Py.AsList(Py.G(e, "business_terms")).Select(Py.Str).ToList(), nonXsd[i].BusinessTerms);
            Assert.Empty(r.RulesNotEvaluated!);
        }
    }

    /// <summary>Verdicts on the public corpus shared with the W System validator image.</summary>
    public class SharedCorpusTests
    {
        private static string Dir => Path.Combine(CoreEngineTests.Dir, "shared");
        private static readonly object? Exp = Json.Decode(File.ReadAllText(Path.Combine(Dir, "expected.json")));

        public static IEnumerable<object[]> Files() => ((Dictionary<string, object?>)Py.G(Exp, "files")!).Keys.Select(k => new object[] { k });

        [Theory]
        [MemberData(nameof(Files))]
        public void Verdict(string file)
        {
            var e = Py.G(Py.G(Exp, "files"), file);
            var bytes = File.ReadAllBytes(Path.Combine(Dir, file));
            var v = new Validator();
            if (Py.T(Py.G(e, "input_error")))
            {
                Assert.ThrowsAny<InputError>(() => v.Validate(bytes));
                return;
            }
            var r = v.Validate(bytes);
            var ids = r.Errors.Select(i => i.RuleId).Distinct().ToList();
            Assert.True((bool)Py.G(e, "valid")! == r.Valid, "verdict; errors: " + string.Join(", ", ids));
            Assert.Equal(Py.Str(Py.G(e, "profile")), r.Profile);
            Assert.Equal(Py.Str(Py.G(e, "syntax")), r.Syntax);
            Assert.Empty(Py.AsList(Py.G(e, "errors")).Select(Py.Str).Except(ids));
        }

        [Fact]
        public void CorpusIsTheSharedOne() => Assert.Equal(17, ((Dictionary<string, object?>)Py.G(Exp, "files")!).Count);
    }

    /// <summary>Every generated fixture validates locally like the reference API (same error and warning rule ids).</summary>
    public class GeneratedValidationTests
    {
        private static readonly object? Expected = Json.Decode(File.ReadAllText(ParityTests.Fx("expected.json")));
        public static IEnumerable<object[]> Cases() => ((Dictionary<string, object?>)Expected!).Where(k => !Py.Has(k.Value, "error")).Select(k => new object[] { k.Key });
        private static readonly Validator V = new Validator();

        [Theory]
        [MemberData(nameof(Cases))]
        public void SameRuleIdsAsApi(string name)
        {
            var exp = Py.G(Expected, name);
            var r = V.Validate(System.Text.Encoding.UTF8.GetBytes(Py.Str(Py.G(exp, "xml"))));
            var wantE = Py.AsList(Py.G(exp, "error_ids")).Select(Py.Str).Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToList();
            var wantW = Py.AsList(Py.G(exp, "warning_ids")).Select(Py.Str).Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToList();
            Assert.Equal(wantE, r.Errors.Select(i => i.RuleId).Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToList());
            Assert.Equal(wantW, r.Warnings.Select(i => i.RuleId).Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToList());
            Assert.Empty(r.RulesNotEvaluated!);
        }
    }
}
