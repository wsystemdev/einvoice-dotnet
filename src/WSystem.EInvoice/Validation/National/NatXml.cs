using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Validation.National
{
    /// <summary>ElementTree-style helpers for the national checks.</summary>
    internal sealed class NatXml
    {
        private readonly XmlNamespaceManager _ns;
        public NatXml(XmlNamespaceManager ns) { _ns = ns; }

        public XmlElement? Find(XmlNode? n, string path) => n?.SelectSingleNode(path, _ns) as XmlElement;

        public List<XmlElement> FindAll(XmlNode? n, string path)
        {
            var o = new List<XmlElement>();
            if (n == null) return o;
            foreach (XmlNode e in n.SelectNodes(path, _ns)!) if (e is XmlElement el) o.Add(el);
            return o;
        }

        public string T(XmlNode? n, string path)
        {
            var e = Find(n, path);
            return e == null ? "" : Document.PyStrip(e.InnerText);
        }

        private static readonly Regex Numeric = new Regex(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);

        /// <summary>"19.00" -> "19", "19.5" -> "19.5", non-numbers unchanged</summary>
        public static string Num(string s)
        {
            var v = s.Trim(' ', '\t', '\n', '\r', '\0', '\x0B');
            if (!Numeric.IsMatch(v)) return s;
            var f = WSystem.EInvoice.Support.Num.ParseDouble(v);
            if (!double.IsInfinity(f) && !double.IsNaN(f) && f == Math.Floor(f) && Math.Abs(f) < 1e18) return ((long)f).ToString(CultureInfo.InvariantCulture);
            return Py.FloatRepr(f);
        }

        public static ValidationIssue W(string ruleset, string rid, string msg, IEnumerable<string> bts, string fix, string? location = null)
            => new ValidationIssue { RuleId = rid, Severity = "warning", Ruleset = ruleset, Message = msg, Location = location, BusinessTerms = bts.ToList(), Fix = fix };

        public static string Up(string s) => Py.Upper(s);

        public static string Attr(XmlElement e, string name) => e.GetAttribute(name);
    }
}
