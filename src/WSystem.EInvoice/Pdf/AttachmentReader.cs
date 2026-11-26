using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Pdf
{
    /// <summary>
    /// Reads embedded files (attachments) from a PDF without a PDF library: classic objects, object streams (PDF 1.5+)
    /// and FlateDecode streams. Enough for ZUGFeRD / Factur-X / XRechnung hybrids; encrypted PDFs are refused.
    /// </summary>
    public sealed class AttachmentReader
    {
        private readonly string _pdf; // Latin-1 view of the bytes
        private readonly Dictionary<int, int> _offsets = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _packed = new Dictionary<int, string>();
        private static readonly Regex ObjRe = new Regex(@"(?<![0-9])([0-9]{1,10})\s+([0-9]{1,5})\s+obj\b", RegexOptions.CultureInvariant);

        private AttachmentReader(string pdf)
        {
            _pdf = pdf;
            foreach (Match m in ObjRe.Matches(pdf))
                if (long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num) && num <= int.MaxValue)
                    _offsets[(int)num] = m.Index;
            foreach (var n in _offsets.Keys.ToList())
            {
                var (dict, stream) = RawObject(n);
                if (stream != null && Regex.IsMatch(dict, @"/Type\s*/ObjStm\b")) UnpackObjStm(dict, stream);
            }
        }

        /// <summary>Embedded files of a PDF: file name =&gt; bytes, in object order.</summary>
        public static IReadOnlyDictionary<string, byte[]> Read(byte[] pdf)
        {
            var s = Bytes.Latin1(pdf);
            if (Regex.IsMatch(s, @"/Encrypt\s+[0-9]+\s+[0-9]+\s+R")) throw new InputError("could not read PDF: the PDF is encrypted");
            return new AttachmentReader(s).Attachments();
        }

        private Dictionary<string, byte[]> Attachments()
        {
            var outp = new Dictionary<string, byte[]>();
            var nums = _offsets.Keys.Concat(_packed.Keys).Distinct().OrderBy(x => x);
            foreach (var n in nums)
            {
                var dict = DictOf(n);
                if (dict == null) continue;
                var ef = Regex.Match(dict, @"/EF\s*<<(.*?)>>", RegexOptions.Singleline);
                if (!ef.Success) continue;
                var rf = Regex.Match(ef.Groups[1].Value, @"/(?:UF|F)\s+([0-9]+)\s+[0-9]+\s+R");
                if (!rf.Success) continue;
                var name = StringValue(dict, "UF") ?? StringValue(dict, "F") ?? ("attachment-" + n);
                var (sdict, stream) = RawObject(int.Parse(rf.Groups[1].Value, CultureInfo.InvariantCulture));
                if (stream == null) continue;
                if (!outp.ContainsKey(name)) outp[name] = Decode(sdict, stream);
            }
            return outp;
        }

        private string? DictOf(int n)
        {
            if (_packed.TryGetValue(n, out var p)) return p;
            if (!_offsets.ContainsKey(n)) return null;
            return RawObject(n).Dict;
        }

        private (string Dict, string? Stream) RawObject(int n, int depth = 0)
        {
            if (!_offsets.TryGetValue(n, out var start)) return ("", null);
            int head = _pdf.IndexOf("obj", start, StringComparison.Ordinal) + 3;
            int? sPos = FindStreamKeyword(head);
            int ePos = _pdf.IndexOf("endobj", head, StringComparison.Ordinal);
            if (ePos < 0) ePos = _pdf.Length;
            if (sPos == null || sPos > ePos) return (_pdf.Substring(head, ePos - head), null);
            var dict = _pdf.Substring(head, sPos.Value - head);
            int dataStart = sPos.Value + 6;
            if (dataStart < _pdf.Length && _pdf[dataStart] == '\r') dataStart++;
            if (dataStart < _pdf.Length && _pdf[dataStart] == '\n') dataStart++;
            long? len = null;
            var lm = Regex.Match(dict, @"/Length\s+([0-9]+)\s+([0-9]+)\s+R");
            if (lm.Success && depth < 4)
            {
                var (ld, _) = RawObject(int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture), depth + 1);
                len = long.TryParse(ld.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) ? lv : 0;
            }
            else
            {
                lm = Regex.Match(dict, @"/Length\s+([0-9]+)");
                if (lm.Success) len = long.TryParse(lm.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var lv2) ? lv2 : 0;
            }
            if (len == null || len <= 0 || dataStart + len > _pdf.Length
                || !Regex.IsMatch(_pdf.Substring((int)(dataStart + len.Value), Math.Min(30, _pdf.Length - (int)(dataStart + len.Value))), @"^\s*endstream"))
            {
                int end = _pdf.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                int l2 = (end < 0 ? _pdf.Length : end) - dataStart;
                len = _pdf.Substring(dataStart, l2).TrimEnd('\r', '\n').Length;
            }
            return (dict, _pdf.Substring(dataStart, (int)len.Value));
        }

        private int? FindStreamKeyword(int from)
        {
            int depth = 0, n = _pdf.Length;
            for (int i = from; i < n - 5; i++)
            {
                char c = _pdf[i];
                if (c == '<' && _pdf[i + 1] == '<') { depth++; i++; }
                else if (c == '>' && _pdf[i + 1] == '>') { depth--; i++; }
                else if (c == '(') i = SkipLiteral(i);
                else if (depth <= 0 && string.CompareOrdinal(_pdf, i, "stream", 0, 6) == 0) return i;
                else if (depth <= 0 && string.CompareOrdinal(_pdf, i, "endobj", 0, 6) == 0) return null;
            }
            return null;
        }

        private int SkipLiteral(int i)
        {
            int d = 0, n = _pdf.Length;
            for (; i < n; i++)
            {
                char c = _pdf[i];
                if (c == '\\') i++;
                else if (c == '(') d++;
                else if (c == ')') { if (--d == 0) return i; }
            }
            return n;
        }

        private static byte[] Decode(string dict, string data)
        {
            var f = Regex.Match(dict, @"/Filter\s*(?:\[\s*)?/(\w+)");
            if (f.Success)
            {
                if (f.Groups[1].Value != "FlateDecode") throw new InputError("could not read PDF: unsupported stream filter " + f.Groups[1].Value);
                return Bytes.Inflate(Bytes.FromLatin1(data)) ?? throw new InputError("could not read PDF: corrupt compressed stream");
            }
            return Bytes.FromLatin1(data);
        }

        private void UnpackObjStm(string dict, string stream)
        {
            var nm = Regex.Match(dict, @"/N\s+([0-9]+)");
            var fm = Regex.Match(dict, @"/First\s+([0-9]+)");
            if (!nm.Success || !fm.Success) return;
            string data;
            try { data = Bytes.Latin1(Decode(dict, stream)); }
            catch (InputError) { return; }
            int first = int.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture);
            if (first > data.Length) return;
            var pairs = Regex.Split(data.Substring(0, first).Trim(), @"\s+");
            var list = new List<(int Num, int Off)>();
            int count = int.Parse(nm.Groups[1].Value, CultureInfo.InvariantCulture);
            for (int k = 0; k + 1 < pairs.Length && k / 2 < count; k += 2)
                if (int.TryParse(pairs[k], NumberStyles.None, CultureInfo.InvariantCulture, out var a) && int.TryParse(pairs[k + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                    list.Add((a, b));
            for (int i = 0; i < list.Count; i++)
            {
                var (num, off) = list[i];
                int end = i + 1 < list.Count ? list[i + 1].Off : data.Length - first;
                if (!_offsets.ContainsKey(num) && first + off <= data.Length && end >= off)
                    _packed[num] = data.Substring(first + off, Math.Min(end - off, data.Length - first - off));
            }
        }

        private static string? StringValue(string dict, string key)
        {
            var m = Regex.Match(dict, "/" + key + @"\s*([(<])");
            if (!m.Success) return null;
            int pos = m.Groups[1].Index;
            var raw = new StringBuilder();
            if (m.Groups[1].Value == "<")
            {
                int end = dict.IndexOf('>', pos);
                var hex = Regex.Replace(dict.Substring(pos + 1, (end < 0 ? dict.Length : end) - pos - 1), "[^0-9A-Fa-f]", "");
                if (hex.Length % 2 == 1) hex = hex.Substring(0, hex.Length - 1);
                for (int i = 0; i < hex.Length; i += 2) raw.Append((char)Convert.ToByte(hex.Substring(i, 2), 16));
            }
            else
            {
                int depth = 0;
                for (int i = pos; i < dict.Length; i++)
                {
                    char c = dict[i];
                    if (c == '\\')
                    {
                        char nx = ++i < dict.Length ? dict[i] : '\0';
                        switch (nx)
                        {
                            case 'n': raw.Append('\n'); break;
                            case 'r': raw.Append('\r'); break;
                            case 't': raw.Append('\t'); break;
                            case 'b': raw.Append('\b'); break;
                            case 'f': raw.Append('\f'); break;
                            case '(': case ')': case '\\': raw.Append(nx); break;
                            default:
                                if (nx >= '0' && nx <= '9')
                                {
                                    var oct = nx.ToString();
                                    while (oct.Length < 3 && i + 1 < dict.Length && dict[i + 1] >= '0' && dict[i + 1] <= '9') oct += dict[++i];
                                    int v = 0;
                                    foreach (var o in oct) v = v * 8 + (o - '0');
                                    raw.Append((char)(v & 0xff));
                                }
                                break;
                        }
                        continue;
                    }
                    if (c == '(') { if (depth++ == 0) continue; }
                    else if (c == ')') { if (--depth == 0) break; }
                    raw.Append(c);
                }
            }
            var bytes = Bytes.FromLatin1(raw.ToString());
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try { return Bytes.Utf8Strict.GetString(bytes); }
            catch (DecoderFallbackException) { return raw.ToString(); }
        }
    }
}
