using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Model
{
    /// <summary>Constants and helpers shared by the UBL and CII builders.</summary>
    internal static class Codes
    {
        public const string EN16931_ID = "urn:cen.eu:en16931:2017";
        public const string XRECHNUNG_ID = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
        public const string XRECHNUNG_EXT_ID = XRECHNUNG_ID + "#conformant#urn:xeinkauf.de:kosit:extension:xrechnung_3.0";
        public const string PEPPOL_ID = "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";
        public const string PEPPOL_PROFILE = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";

        public static readonly Dictionary<string, string> NS = new Dictionary<string, string>
        {
            ["ubl_inv"] = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2",
            ["ubl_cn"] = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2",
            ["cii"] = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100",
            ["cbc"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
            ["cac"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
            ["rsm"] = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100",
            ["ram"] = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100",
            ["udt"] = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100",
            ["qdt"] = "urn:un:unece:uncefact:data:standard:QualifiedDataType:100",
            ["svrl"] = "http://purl.oclc.org/dsdl/svrl",
        };

        public static readonly Dictionary<string, (string? Code, string Text)> EXEMPT_DEFAULTS = new Dictionary<string, (string?, string)>
        {
            ["AE"] = ("VATEX-EU-AE", "Reverse charge"),
            ["K"] = ("VATEX-EU-IC", "Intra-Community supply"),
            ["G"] = ("VATEX-EU-G", "Export outside the EU"),
            ["O"] = ("VATEX-EU-O", "Not subject to VAT"),
            ["E"] = (null, "Exempt from VAT"),
        };
        public static readonly HashSet<string> NO_TAX = new HashSet<string> { "O", "E", "AE", "K", "G", "Z" };
        public static readonly HashSet<string> CN_CODES = new HashSet<string> { "381", "396", "81", "83", "532" };

        public static readonly Dictionary<string, string> VAT_SCHEME = new Dictionary<string, string>
        {
            ["AT"] = "9915", ["BE"] = "0208", ["DE"] = "9930", ["DK"] = "0184", ["ES"] = "9920", ["FR"] = "9957", ["GR"] = "9933",
            ["HU"] = "9910", ["IE"] = "9935", ["IT"] = "0211", ["LU"] = "9938", ["NL"] = "9944", ["PL"] = "9945", ["PT"] = "9946",
            ["RO"] = "9947", ["SE"] = "0007", ["SI"] = "9949", ["SK"] = "9950", ["CZ"] = "9929", ["EE"] = "9931", ["FI"] = "0216",
            ["HR"] = "9934", ["LT"] = "9937", ["LV"] = "9939", ["MT"] = "9943", ["BG"] = "9926", ["CY"] = "9928", ["NO"] = "0192",
        };
        public static readonly Dictionary<string, string> RETIRED_UNITS = new Dictionary<string, string> { ["PK"] = "XPK" };

        public const string DISCLAIMER = "Technical check against the official EN 16931 / XRechnung rule sets. A technically valid file is not tax or legal advice. "
            + "Peppol BIS 3 specific rules (PEPPOL-EN16931-*) are not checked yet.";
        public const string NO_VALIDATION_DISCLAIMER = "Not validated: validation was turned off (NoValidation), so neither the XML Schema nor the EN 16931 / XRechnung rules were checked. "
            + "Validate the file before sending it. A file is not tax or legal advice.";

        public static PyDecimal Dec(object? v, string field)
        {
            try { return PyDecimal.Of(v); }
            catch (FormatException) { throw new InputError(field + " must be a number"); }
        }

        private static readonly Regex DateRe = new Regex(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant);
        public static bool IsDate(object? v) => DateRe.IsMatch(Py.Str(v)) && Ascii(Py.Str(v));
        private static bool Ascii(string s) { foreach (var c in s) if (c > 127) return false; return true; }

        private static readonly Regex Pid = new Regex(@"^(?:iso6523-actorid-upis::)?([0-9]{4}):([^\s:@]+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static (string Value, string Scheme)? SplitParticipantId(object? s)
        {
            var str = Py.Trim(Py.Str(Py.T(s) ? s : ""));
            var m = Pid.Match(str);
            return m.Success ? (m.Groups[2].Value, m.Groups[1].Value) : ((string, string)?)null;
        }

        private static bool AllDigits(string s, int len) { if (s.Length != len) return false; foreach (var c in s) if (c < '0' || c > '9') return false; return true; }

        public static (object? Id, object? Scheme) Endpoint(object? p, string fmt, string role)
        {
            if (Py.T(Py.G(p, "endpoint_id")))
            {
                var sp = SplitParticipantId(Py.G(p, "endpoint_id"));
                if (sp != null)
                {
                    if (Py.T(Py.G(p, "endpoint_scheme")) && Py.Trim(Py.Str(Py.G(p, "endpoint_scheme"))) != sp.Value.Scheme)
                        throw new InputError($"{role}.endpoint_id carries scheme {sp.Value.Scheme} but {role}.endpoint_scheme is {Py.Str(Py.G(p, "endpoint_scheme"))}"
                            + "; send the Peppol id as endpoint_scheme + endpoint_id, or the full id alone");
                    return (sp.Value.Value, sp.Value.Scheme);
                }
                var eid = Py.G(p, "endpoint_id");
                var sch = Py.T(Py.G(p, "endpoint_scheme")) ? Py.G(p, "endpoint_scheme") : (Py.Str(eid).Contains("@") ? "EM" : "0088");
                return (eid, sch);
            }
            var vat = Py.Upper(Py.Str(Py.T(Py.G(p, "vat_id")) ? Py.G(p, "vat_id") : "").Replace(" ", ""));
            if (fmt == "peppol-bis3")
            {
                string cc = Substr(vat, 0, 2), num = Substr(vat, 2);
                if (cc == "BE" && num.Length > 0 && AllDigitsAny(num)) return (num, "0208");
                if (cc == "SK" && AllDigits(num, 10)) return (num, "0245");
                if (cc == "NO" && (AllDigits(num, 9) || (num.Length == 12 && AllDigits(num.Substring(0, 9), 9) && num.EndsWith("MVA", StringComparison.Ordinal)))) return (num.Substring(0, 9), "0192");
                if (cc == "DK" && AllDigits(num, 8)) return (num, "0184");
                if (cc == "SE" && AllDigits(num, 12)) return (num.Substring(0, 10), "0007");
                if (cc == "FI" && AllDigits(num, 8)) return ("0037" + num, "0216");
                if (VAT_SCHEME.ContainsKey(cc) && vat.Length > 0) return (vat, VAT_SCHEME[cc]);
                throw new InputError(role + ".endpoint_id and endpoint_scheme are required for Peppol (could not derive from VAT ID)");
            }
            if (Py.T(Py.G(p, "email"))) return (Py.G(p, "email"), "EM");
            return (null, null);
        }

        private static bool AllDigitsAny(string s) { foreach (var c in s) if (c < '0' || c > '9') return false; return true; }

        /// <summary>PHP substr on bytes; inputs here are ASCII-upper VAT ids, so chars are fine.</summary>
        public static string Substr(string s, int start, int? len = null)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            if (start >= b.Length) return "";
            int l = len ?? (b.Length - start);
            l = Math.Min(l, b.Length - start);
            return System.Text.Encoding.UTF8.GetString(b, start, l);
        }

        public static object? Unit(object? u)
        {
            u = Py.T(u) ? u : "C62";
            return RETIRED_UNITS.TryGetValue(Py.Upper(Py.Trim(Py.Str(u))), out var r) ? r : u;
        }
    }
}
