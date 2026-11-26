using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Validation;
using Xunit;

namespace WSystem.EInvoice.Tests
{
    /// <summary>KoSIT scenario severity levels (scenarios.xml customLevel) for standard XRechnung and XRechnung CVD, as in the
    /// W System engine 0.3.157.1: the official CVD samples (BR-CL-13) and unlisted unit codes (BR-CL-23) are warnings, the
    /// overrides are specific to their scenario, and "error" levels escalate Schematron warnings.</summary>
    public class KositLevelsTests
    {
        private static string Shared => Path.Combine(CoreEngineTests.Dir, "shared", "valid");
        private static readonly Validator V = new Validator();

        private static byte[] Swap(string file, string from, string to) =>
            Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(Shared, file), Encoding.UTF8).Replace(from, to));

        [Theory]
        [InlineData("KoSIT_02.01a-cvd_INVOICE_ubl.xml")]
        [InlineData("KoSIT_02.01a-cvd_INVOICE_uncefact.xml")]
        public void OfficialCvdSamplesAreValidWithBrCl13Warning(string file)
        {
            var r = V.Validate(File.ReadAllBytes(Path.Combine(Shared, file)));
            Assert.True(r.Valid, string.Join(", ", r.Errors.Select(e => e.RuleId)));
            Assert.Contains(r.Warnings, w => w.RuleId == "BR-CL-13" && (w.ProfileNote ?? "").Contains("KoSIT"));
            var std = V.Validate(Swap(file, Codes.XRECHNUNG_CVD_ID, Codes.XRECHNUNG_ID));
            Assert.False(std.Valid);
            Assert.Contains(std.Errors, e => e.RuleId == "BR-CL-13");
        }

        [Theory]
        [InlineData("ubl-xrechnung-BR-CL-23-unit-code-warning.xml")]
        [InlineData("cii-xrechnung-BR-CL-23-unit-code-warning.xml")]
        public void BrCl23IsAWarningInStandardXrechnungOnly(string file)
        {
            var r = V.Validate(File.ReadAllBytes(Path.Combine(Shared, file)));
            Assert.True(r.Valid, string.Join(", ", r.Errors.Select(e => e.RuleId)));
            Assert.Contains(r.Warnings, w => w.RuleId == "BR-CL-23");
            var g = V.Validate(Swap(file, Codes.XRECHNUNG_ID, Codes.EN16931_ID));
            Assert.False(g.Valid);
            Assert.Contains(g.Errors, e => e.RuleId == "BR-CL-23");
        }

        [Fact]
        public void ScenarioSelectionIsExact()
        {
            Assert.Equal("XRechnung", Issues.KositLevels("XRechnung", "ubl", "ubl_inv", Codes.XRECHNUNG_ID)!.Value.Scenario);
            Assert.Equal("XRechnung CVD", Issues.KositLevels("XRechnung", "cii", "cii", Codes.XRECHNUNG_CVD_ID)!.Value.Scenario);
            Assert.Equal("Extension", Issues.KositLevels("XRechnung Extension", "cii", "cii", Codes.XRECHNUNG_EXT_ID)!.Value.Scenario);
            Assert.Null(Issues.KositLevels("XRechnung", "ubl", "ubl_inv", Codes.XRECHNUNG_ID + "#other"));
            Assert.Null(Issues.KositLevels("EN 16931", "ubl", "ubl_inv", Codes.EN16931_ID));
        }

        [Fact]
        public void ErrorLevelEscalatesAndOtherRulesAreUntouched()
        {
            var i = Issues.KositLevel(new ValidationIssue { RuleId = "CII-SR-465", Severity = "warning", Ruleset = "EN16931" }, Issues.XRECHNUNG_STD_LEVELS["cii"], "XRechnung");
            Assert.Equal("error", i.Severity);
            Assert.Contains("customLevel: error", i.ProfileNote);
            var same = new ValidationIssue { RuleId = "BR-CO-10", Severity = "error", Ruleset = "EN16931" };
            Assert.Same(same, Issues.KositLevel(same, Issues.XRECHNUNG_STD_LEVELS["cii"], "XRechnung"));
        }

        [Fact]
        public void TablesMatchTheBundledKositScenarios()
        {
            var root = System.AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "WSystem.EInvoice.sln"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
            var t = File.ReadAllText(Path.Combine(root, "src", "WSystem.EInvoice", "Resources", "kosit", "xrechnung-3.0.2-2026-08-31", "scenarios.xml"));
            var std = new Dictionary<string, Dictionary<string, string>>();
            var cvd = new Dictionary<string, Dictionary<string, string>>();
            foreach (Match sc in Regex.Matches(t, "<scenario>(.*?)</scenario>", RegexOptions.Singleline))
            {
                var name = Regex.Match(sc.Groups[1].Value, "<name>(.*?)</name>").Groups[1].Value;
                if (!name.Contains("XRechnung") || name.Contains("Extension")) continue;
                var dt = name.Contains("(CII)") ? "cii" : name.Contains("CreditNote") ? "ubl_cn" : "ubl_inv";
                var lv = Regex.Matches(sc.Groups[1].Value, "<customLevel level=\"(\\w+)\">([\\w-]+)</customLevel>").Cast<Match>()
                    .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);
                (name.Contains("CVD") ? cvd : std)[dt] = lv;
            }
            foreach (var (got, want) in new[] { (std, Issues.XRECHNUNG_STD_LEVELS), (cvd, Issues.XRECHNUNG_CVD_LEVELS) })
            {
                Assert.Equal(want.Keys.OrderBy(k => k), got.Keys.OrderBy(k => k));
                foreach (var k in want.Keys)
                    Assert.Equal(want[k].OrderBy(x => x.Key), got[k].OrderBy(x => x.Key));
            }
        }
    }
}
