using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using WSystem.EInvoice.Model;

namespace WSystem.EInvoice.Support
{
    /// <summary>
    /// Helpers that reproduce the W System reference engine's semantics exactly (truthiness, str(), XML escaping),
    /// so .NET, PHP and API output are byte-identical. Invoice input is held as plain values:
    /// null, bool, long, double, string, <see cref="Dictionary{TKey,TValue}"/> (JSON object) and <see cref="List{T}"/> (JSON array).
    /// </summary>
    internal static class Py
    {
        public static bool T(object? v)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case string s: return s.Length > 0;
                case long l: return l != 0;
                case int i: return i != 0;
                case double d: return d != 0.0;
                case decimal m: return m != 0m;
                case PyDecimal pd: return !pd.IsZero;
                case Dictionary<string, object?> dict: return dict.Count > 0;
                case List<object?> list: return list.Count > 0;
                default: return true;
            }
        }

        /// <summary>a[k] ?? null for a JSON object (null when a is not an object or k is missing).</summary>
        public static object? G(object? a, string k)
        {
            if (a is Dictionary<string, object?> d && d.TryGetValue(k, out var v)) return v;
            return null;
        }

        public static bool Has(object? a, string k) => a is Dictionary<string, object?> d && d.ContainsKey(k);

        /// <summary>truthy value or null</summary>
        public static object? V(object? a, string k)
        {
            var v = G(a, k);
            return T(v) ? v : null;
        }

        /// <summary>truthy a[k], else the default</summary>
        public static object? Or(object? a, string k, object? dflt)
        {
            var v = G(a, k);
            return T(v) ? v : dflt;
        }

        public static string Str(object? v)
        {
            switch (v)
            {
                case null: return "None";
                case bool b: return b ? "True" : "False";
                case string s: return s;
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case double d: return FloatRepr(d);
                case PyDecimal pd: return pd.ToString();
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
                case Dictionary<string, object?> _:
                case List<object?> _:
                    return Json.Encode(v);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }
        }

        /// <summary>Python repr(float).</summary>
        public static string FloatRepr(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsPositiveInfinity(d)) return "inf";
            if (double.IsNegativeInfinity(d)) return "-inf";
            if (d == 0) return (1 / d < 0) ? "-0.0" : "0.0";
            string r = d.ToString("R", CultureInfo.InvariantCulture); // shortest round-trip
            bool neg = r.StartsWith("-", StringComparison.Ordinal);
            if (neg) r = r.Substring(1);
            // split into digits + decimal exponent
            int ePos = r.IndexOfAny(new[] { 'E', 'e' });
            string mant = ePos >= 0 ? r.Substring(0, ePos) : r;
            int exp = ePos >= 0 ? int.Parse(r.Substring(ePos + 1), CultureInfo.InvariantCulture) : 0;
            int dot = mant.IndexOf('.');
            string digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
            int pointPos = (dot >= 0 ? dot : mant.Length) + exp; // position of decimal point within digits
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointPos--; }
            digits = digits.Substring(lead).TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            int sciExp = pointPos - 1;
            string res;
            if (sciExp < -4 || sciExp >= 16)
            {
                string m = digits.Length > 1 ? digits[0] + "." + digits.Substring(1) : digits;
                res = m + "e" + (sciExp < 0 ? "-" : "+") + Math.Abs(sciExp).ToString("00", CultureInfo.InvariantCulture);
            }
            else if (pointPos <= 0)
            {
                res = "0." + new string('0', -pointPos) + digits;
            }
            else if (pointPos >= digits.Length)
            {
                res = digits + new string('0', pointPos - digits.Length) + ".0";
            }
            else
            {
                res = digits.Substring(0, pointPos) + "." + digits.Substring(pointPos);
            }
            return (neg ? "-" : "") + res;
        }

        /// <summary>xml.sax.saxutils.escape(str(v)): &amp; &lt; &gt; only</summary>
        public static string X(object? v) => Str(v).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        public static string Upper(object? v)
        {
            var s = Str(v);
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch == '\u00DF') sb.Append("SS");
                else sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        public static string Lower(object? v) => Str(v).ToLowerInvariant();

        /// <summary>Python str[:n] on code points</summary>
        public static string Cut(string s, int n)
        {
            int cps = 0, i = 0;
            while (i < s.Length && cps < n)
            {
                i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                cps++;
            }
            return s.Substring(0, i);
        }

        /// <summary>Python str.strip() for the whitespace PHP trim() removes</summary>
        public static string Trim(string s) => s.Trim(' ', '\t', '\n', '\r', '\0', '\x0B');

        public static bool IsDict(object? v) => v is Dictionary<string, object?> || (v is List<object?> l && l.Count == 0);
        public static bool IsList(object? v) => v is List<object?> || (v is Dictionary<string, object?> d && d.Count == 0);

        public static IList<object?> AsList(object? v) => v is List<object?> l ? l : new List<object?>();
        public static Dictionary<string, object?> AsDict(object? v) => v is Dictionary<string, object?> d ? d : new Dictionary<string, object?>();
    }

    /// <summary>JSON &lt;-&gt; plain values, with PHP json_decode/json_encode number semantics.</summary>
    public static class Json
    {
        public static object? Decode(string json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
            return FromElement(doc.RootElement);
        }

        public static object? FromElement(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var d = new Dictionary<string, object?>();
                    foreach (var p in e.EnumerateObject()) d[p.Name] = FromElement(p.Value);
                    return d;
                case JsonValueKind.Array:
                    var l = new List<object?>();
                    foreach (var x in e.EnumerateArray()) l.Add(FromElement(x));
                    return l;
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number:
                    var raw = e.GetRawText();
                    if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lv)) return lv;
                    return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        /// <summary>Plain value (or any object via System.Text.Json) to the plain value model.</summary>
        public static object? Normalize(object? v)
        {
            switch (v)
            {
                case null: return null;
                case string _: case bool _: case long _: case double _: return v;
                case int i: return (long)i;
                case float f: return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture); // exact: keep as a numeric string
                case JsonElement je: return FromElement(je);
                case System.Text.Json.Nodes.JsonNode jn: return Decode(jn.ToJsonString());
                case Dictionary<string, object?> d:
                    var nd = new Dictionary<string, object?>();
                    foreach (var kv in d) nd[kv.Key] = Normalize(kv.Value);
                    return nd;
                case System.Collections.IDictionary id:
                    var od = new Dictionary<string, object?>();
                    foreach (System.Collections.DictionaryEntry kv in id) od[Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!] = Normalize(kv.Value);
                    return od;
                case System.Collections.IEnumerable en:
                    var nl = new List<object?>();
                    foreach (var x in en) nl.Add(Normalize(x));
                    return nl;
                default:
                    return Decode(JsonSerializer.Serialize(v, v.GetType(), new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            }
        }

        /// <summary>json_encode of plain values (PHP default flags: escaped slashes and unicode).</summary>
        public static string Encode(object? v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object? v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(Py.FloatRepr(d).Replace("e+", "e+")); break;
                case string s: WriteString(sb, s); break;
                case PyDecimal pd: WriteString(sb, pd.ToString()); break;
                case Dictionary<string, object?> dict:
                    if (dict.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict) { if (!first) sb.Append(','); first = false; WriteString(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value); }
                    sb.Append('}');
                    break;
                case List<object?> list:
                    sb.Append('[');
                    for (int k = 0; k < list.Count; k++) { if (k > 0) sb.Append(','); Write(sb, list[k]); }
                    sb.Append(']');
                    break;
                default: WriteString(sb, Py.Str(v)); break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '/': sb.Append("\\/"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
