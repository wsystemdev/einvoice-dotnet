using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;

namespace WSystem.EInvoice.Validation
{
    /// <summary>XML Schema check (UBL 2.1 / CII D16B).</summary>
    internal static class XsdCheck
    {
        public const string RULESET = "XML Schema (UBL 2.1 / CII D16B)";
        public const string FIX = "The XML structure is wrong (unknown element, wrong order or bad value). Fix this first; business rules may be unreliable until it passes.";
        private static readonly Regex Braces = new Regex(@"\{[^}]+\}", RegexOptions.CultureInvariant);

        public static List<ValidationIssue> Errors(Document doc, RulePack rules)
        {
            var raw = new List<(int Line, string Msg)>();
            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.Schema, Schemas = rules.Schema(doc.DocumentType),
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                ValidationFlags = XmlSchemaValidationFlags.None,
            };
            settings.ValidationEventHandler += (s, e) =>
            {
                if (e.Severity == XmlSeverityType.Error && raw.Count < 50) raw.Add((e.Exception?.LineNumber ?? 0, e.Message));
            };
            using (var ms = new MemoryStream(doc.Xml))
            using (var r = XmlReader.Create(ms, settings))
                while (r.Read()) { }
            var o = new List<ValidationIssue>();
            foreach (var (line, msg) in raw)
                o.Add(new ValidationIssue
                {
                    RuleId = "XSD", Severity = "error", Ruleset = RULESET, Message = $"Line {line}: " + Braces.Replace(msg.TrimEnd('\n'), ""),
                    Location = null, Fix = FIX,
                });
            return o;
        }
    }
}
