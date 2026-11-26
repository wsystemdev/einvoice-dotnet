using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Validation.Core;
using WSystem.EInvoice.Validation.National;

namespace WSystem.EInvoice.Validation
{
    /// <summary>
    /// Local, offline e-invoice validation: XML Schema + EN 16931 (+ XRechnung 3.0.2) business rules, for UBL 2.1,
    /// UN/CEFACT CII D16B and ZUGFeRD / Factur-X / XRechnung PDF/A-3 hybrids. Same result shape and rule ids as the
    /// W System API's POST /einvoice/validate.
    ///
    /// Business rules run on the pure-.NET engine "dotnet-core": it reads the bundled, unmodified official CEN / KoSIT
    /// schematron (XSLT) files and evaluates their rules with an XPath 2.0 subset engine. It is NOT the official engine:
    /// results are labelled conformance "pre-check" and checked.* = "core-subset". For the official rule run use
    /// <see cref="HttpValidator"/> (the W System validator or API).
    /// </summary>
    public sealed class Validator : IValidator
    {
        public const string ENGINE = "dotnet-core";
        public const string CORE_VERSION = "W System dotnet-core 1.0 (pure-.NET schematron subset engine)";
        public const string DISCLAIMER = Codes.DISCLAIMER;
        private const string CORE_DISCLAIMER = " Business rules were pre-checked by the pure-.NET engine (dotnet-core), not by the official schematron engine: "
            + "a valid result here is a pre-check, not a conformance check. For the complete official rule sets, "
            + "use the W System validator or API (HttpValidator).";

        private readonly RulePack _rules;
        private readonly int _maxBytes;

        public Validator(RulePack? rules = null, int maxBytes = Document.DEFAULT_MAX_BYTES)
        {
            _rules = rules ?? new RulePack();
            _maxBytes = maxBytes;
        }

        public RulePack Rules => _rules;

        /// <summary>Validates locally. Rule failures are returned in the result; input that is not an e-invoice throws.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="InputError">The input is empty, too large, not well-formed XML, not a UBL / CII invoice, a PDF
        /// without a readable embedded invoice XML (including PDFs whose decompressed data exceeds 75 MB), or
        /// <paramref name="rules"/> is unknown.</exception>
        public ValidationResult Validate(byte[] data, string rules = "auto")
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var sw = Stopwatch.StartNew();
            var doc = Document.Load(data, _maxBytes);
            rules = (rules == "" ? "auto" : rules).ToLowerInvariant();
            var sets = new List<string> { "EN16931" };
            if (rules == "xrechnung" || (rules == "auto" && (doc.Profile == "XRechnung" || doc.Profile == "XRechnung Extension"))) sets.Add("XRechnung");
            else if (rules != "auto" && rules != "en16931") throw new InputError("rules must be auto, en16931 or xrechnung (peppol is not available yet)");

            var notEvaluated = new List<RuleNotEvaluated>();
            var issues = XsdCheck.Errors(doc, _rules);
            Tree? tree = null;
            foreach (var rs in sets)
            {
                try
                {
                    issues.AddRange(RunCore(doc, rs, ref tree, notEvaluated));
                }
                catch (RuleEvaluationError e)
                {
                    throw e.WithXsdErrors(issues.ToList());
                }
            }
            if (doc.Syntax == "cii" && doc.CustomizationId.ToLowerInvariant().Contains("extended")) issues = issues.Select(Issues.ExtendedRelax).ToList();
            if (doc.Profile == "XRechnung Extension" && sets.Contains("XRechnung")) issues = issues.Select(i => Issues.XrechnungExtLevel(i, doc.Syntax)).ToList();
            var (skIssues, sk) = Slovakia.Check(doc);
            var (noIssues, no) = Norway.Check(doc);
            issues.AddRange(skIssues); issues.AddRange(noIssues);
            var errors = issues.Where(i => i.Severity == "error").ToList();
            var warnings = issues.Where(i => i.Severity == "warning").ToList();
            int fmt = 0;
            foreach (var e in errors)
            {
                e.ErrorClass = e.RuleId == "XSD" ? "format" : "business_rule";
                if (e.RuleId == "XSD") fmt++;
            }
            var notes = doc.Notes.ToList();
            foreach (var blk in new[] { sk, no }) if (blk.TryGetValue("note", out var n) && n is string ns) notes.Add(ns);
            var ruleVersions = new Dictionary<string, string>();
            if (!_rules.IsBundled)
            {
                ruleVersions["rule_pack_dir"] = _rules.Dir;
                ruleVersions["note"] = "rule files from a rule pack directory, not the bundled pack";
            }
            foreach (var kv in RulePack.RuleVersions()) ruleVersions[kv.Key] = kv.Value;
            ruleVersions["core_rules"] = CORE_VERSION;
            var skApplies = (bool)sk["applies"]!; var noApplies = (bool)no["applies"]!;
            if (skApplies) ruleVersions["SK_national"] = Slovakia.TRANSPOSITION;
            if (noApplies) ruleVersions["NO_national"] = Norway.VERSION;
            const string mark = "core-subset";
            return new ValidationResult
            {
                Valid = errors.Count == 0, ErrorCount = errors.Count, WarningCount = warnings.Count,
                Container = doc.Container, EmbeddedFile = doc.EmbeddedFile,
                Syntax = doc.Syntax, DocumentType = doc.DocumentType, CustomizationId = doc.CustomizationId, Profile = doc.Profile,
                Notes = notes,
                Checked = new Dictionary<string, object>
                {
                    ["xsd"] = true, ["EN16931"] = mark, ["XRechnung"] = sets.Contains("XRechnung") ? mark : (object)false,
                    ["PeppolBIS3"] = false, ["SK_national"] = skApplies, ["NO_national"] = noApplies,
                },
                Errors = errors, Warnings = warnings,
                Engine = ENGINE, Backend = ENGINE, Conformance = "pre-check",
                RuleSet = RuleSetLabel(sets),
                RuleVersions = ruleVersions,
                Sources = RulePack.SOURCES.ToDictionary(k => k.Key, k => k.Value),
                Disclaimer = DISCLAIMER + CORE_DISCLAIMER,
                FormatErrorCount = fmt, BusinessRuleErrorCount = errors.Count - fmt,
                Ms = sw.ElapsedMilliseconds,
                RulesNotEvaluated = notEvaluated,
                Slovakia = skApplies ? sk : null,
                Norway = noApplies ? no : null,
            };
        }

        private List<ValidationIssue> RunCore(Document doc, string ruleset, ref Tree? tree, List<RuleNotEvaluated> notEvaluated)
        {
            var rel = _rules.XslPath(doc.Syntax, ruleset);
            var prog = SchematronProgram.Load(_rules.Key(rel), () => _rules.LoadXml(rel));
            tree ??= new Tree(doc.Dom);
            List<Fail> fails; List<EvalProblem> errs;
            try
            {
                (fails, errs) = prog.Run(tree);
            }
            catch (XPathError e)
            {
                throw new RuleEvaluationError(e.Message, ENGINE, ruleset);
            }
            foreach (var kv in prog.Unsupported)
                notEvaluated.Add(new RuleNotEvaluated { RuleId = kv.Key, Ruleset = ruleset, Reason = "not supported by dotnet-core: " + kv.Value });
            foreach (var e in errs)
                notEvaluated.Add(new RuleNotEvaluated { RuleId = e.Id, Ruleset = ruleset, Reason = "evaluation error: " + e.Error });
            return fails.Select(f => Issues.Entry(f.Id, f.Flag, ruleset, f.Text, f.Location)).ToList();
        }

        private static string RuleSetLabel(List<string> sets)
        {
            var names = string.Join(" + ", sets.Select(s => s == "XRechnung" ? "XRechnung 3.0.2" : "EN 16931"));
            return $"{names}: pre-check by the W System pure-.NET engine over the bundled KoSIT/CEN rule files ({CORE_VERSION}), not the official engine";
        }
    }
}
