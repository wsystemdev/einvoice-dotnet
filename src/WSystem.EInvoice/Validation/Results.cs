using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSystem.EInvoice.Validation
{
    /// <summary>One finding: a failed EN 16931 / XRechnung rule, an XML Schema error or a national field check.</summary>
    public sealed class ValidationIssue
    {
        [JsonPropertyName("rule_id")] public string RuleId { get; set; } = "";
        /// <summary>"error" or "warning"</summary>
        [JsonPropertyName("severity")] public string Severity { get; set; } = "error";
        [JsonPropertyName("ruleset")] public string Ruleset { get; set; } = "";
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("business_terms")] public List<string> BusinessTerms { get; set; } = new List<string>();
        [JsonPropertyName("fix")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Fix { get; set; }
        /// <summary>"format" (XML Schema) or "business_rule"; set on errors.</summary>
        [JsonPropertyName("error_class")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ErrorClass { get; set; }
        [JsonPropertyName("profile_note")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProfileNote { get; set; }

        internal ValidationIssue Clone() => (ValidationIssue)MemberwiseClone();
    }

    /// <summary>A rule the pure-.NET engine could not evaluate (dotnet-core only).</summary>
    public sealed class RuleNotEvaluated
    {
        [JsonPropertyName("rule_id")] public string RuleId { get; set; } = "";
        [JsonPropertyName("ruleset")] public string Ruleset { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }

    /// <summary>Same shape as the W System API's POST /einvoice/validate result.</summary>
    public sealed class ValidationResult
    {
        [JsonPropertyName("valid")] public bool Valid { get; set; }
        [JsonPropertyName("error_count")] public int ErrorCount { get; set; }
        [JsonPropertyName("warning_count")] public int WarningCount { get; set; }
        /// <summary>"xml" or "pdf"</summary>
        [JsonPropertyName("container")] public string Container { get; set; } = "xml";
        [JsonPropertyName("embedded_file")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EmbeddedFile { get; set; }
        /// <summary>"ubl" or "cii"</summary>
        [JsonPropertyName("syntax")] public string Syntax { get; set; } = "";
        /// <summary>"ubl_inv", "ubl_cn" or "cii"</summary>
        [JsonPropertyName("document_type")] public string DocumentType { get; set; } = "";
        [JsonPropertyName("customization_id")] public string CustomizationId { get; set; } = "";
        [JsonPropertyName("profile")] public string Profile { get; set; } = "";
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new List<string>();
        /// <summary>xsd: bool; EN16931 / XRechnung: true (official), "core-subset" (pre-check) or false; PeppolBIS3: false; SK_national, NO_national: bool.</summary>
        [JsonPropertyName("checked")] public Dictionary<string, object> Checked { get; set; } = new Dictionary<string, object>();
        [JsonPropertyName("errors")] public List<ValidationIssue> Errors { get; set; } = new List<ValidationIssue>();
        [JsonPropertyName("warnings")] public List<ValidationIssue> Warnings { get; set; } = new List<ValidationIssue>();
        /// <summary>"dotnet-core" (pure .NET pre-check) or "remote" (W System validator / API, official rules)</summary>
        [JsonPropertyName("engine")] public string Engine { get; set; } = "";
        [JsonPropertyName("backend")] public string Backend { get; set; } = "";
        /// <summary>"official" or "pre-check"</summary>
        [JsonPropertyName("conformance")] public string Conformance { get; set; } = "";
        [JsonPropertyName("rule_set")] public string RuleSet { get; set; } = "";
        [JsonPropertyName("rule_versions")] public Dictionary<string, string> RuleVersions { get; set; } = new Dictionary<string, string>();
        [JsonPropertyName("sources")] public Dictionary<string, string> Sources { get; set; } = new Dictionary<string, string>();
        [JsonPropertyName("disclaimer")] public string Disclaimer { get; set; } = "";
        [JsonPropertyName("format_error_count")] public int FormatErrorCount { get; set; }
        [JsonPropertyName("business_rule_error_count")] public int BusinessRuleErrorCount { get; set; }
        [JsonPropertyName("ms")] public long Ms { get; set; }
        [JsonPropertyName("rules_not_evaluated")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<RuleNotEvaluated>? RulesNotEvaluated { get; set; }
        [JsonPropertyName("remote_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RemoteUrl { get; set; }
        [JsonPropertyName("slovakia")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, object?>? Slovakia { get; set; }
        [JsonPropertyName("norway")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, object?>? Norway { get; set; }

        /// <summary>The result as JSON (same field names as the W System API).</summary>
        public string ToJson(bool indented = false) => System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = indented });
    }

    /// <summary>Validates e-invoice bytes (UBL / CII XML, or a ZUGFeRD / Factur-X / XRechnung PDF).</summary>
    public interface IValidator
    {
        /// <param name="data">invoice bytes</param>
        /// <param name="rules">"auto" (XRechnung rules for XRechnung files, EN 16931 otherwise), "en16931" or "xrechnung"</param>
        ValidationResult Validate(byte[] data, string rules = "auto");
    }

    /// <summary>
    /// The business rules could not be evaluated on this document: an official rule hit a dynamic XPath error, almost
    /// always because a value has the wrong type (which the XML Schema check also reports). The official engine aborts
    /// the whole rule run in that case, and so does every backend here, so no partial result can look valid.
    /// </summary>
    public sealed class RuleEvaluationError : InputError
    {
        public string Detail { get; }
        public string Engine { get; }
        public string Ruleset { get; }
        public IReadOnlyList<ValidationIssue> XsdErrors { get; }

        public RuleEvaluationError(string detail, string engine, string ruleset, IReadOnlyList<ValidationIssue>? xsdErrors = null)
            : base(Msg(detail, engine, ruleset, xsdErrors))
        {
            Detail = detail; Engine = engine; Ruleset = ruleset; XsdErrors = xsdErrors ?? new List<ValidationIssue>();
        }

        private static string Msg(string detail, string engine, string ruleset, IReadOnlyList<ValidationIssue>? x)
        {
            var xsd = x != null && x.Count > 0 ? " XML Schema: " + x[0].Message + (x.Count > 1 ? " (+" + (x.Count - 1) + " more)" : "") : "";
            return $"The {ruleset} rules could not be evaluated on this document ({engine}): {detail}. "
                + "This usually means a value has the wrong type; fix the XML Schema errors first." + xsd;
        }

        internal RuleEvaluationError WithXsdErrors(IReadOnlyList<ValidationIssue> xsd) => new RuleEvaluationError(Detail, Engine, Ruleset, xsd);
    }
}
