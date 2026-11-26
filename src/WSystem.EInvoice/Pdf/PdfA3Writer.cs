using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSystem.EInvoice.Support;
using WSystem.EInvoice.Validation;

namespace WSystem.EInvoice.Pdf
{
    /// <summary>
    /// A minimal PDF writer (a C# port of the subset of FPDF 1.9 by Olivier Plathey, permissive FPDF licence, that the
    /// invoice layout uses) plus what PDF/A-3b needs: PDF 1.7 header with binary marker, embedded TrueType fonts only,
    /// XMP metadata (pdfaid 3/B + Factur-X extension schema) matching the Info dictionary, sRGB output intent, an
    /// embedded file with /AFRelationship referenced from the catalog /AF array, and a trailer /ID.
    /// Text passed to Cell / GetStringWidth is already Windows-1252 encoded (one char per byte, see <see cref="Enc"/>).
    /// </summary>
    internal sealed class PdfA3Writer
    {
        public const string PRODUCER = "W System e-invoice SDK for .NET";
        private const double K = 72 / 25.4;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Font
        {
            public int I, N;
            public string Type = "", Name = "", File = "";
            public bool Subsetted;
            public string? Enc;
            public int[] Cw = new int[256];
            public List<(string Key, string Value)> Desc = new List<(string, string)>();
            public List<(int Code, int Start, int Count, bool Range)> Uv = new List<(int, int, int, bool)>();
            public int OriginalSize;
        }

        // ---- FPDF state
        private int _page, _n = 2, _state;
        private readonly Dictionary<int, int> _offsets = new Dictionary<int, int>();
        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly List<StringBuilder> _pages = new List<StringBuilder> { new StringBuilder() };
        private readonly Dictionary<int, int> _pageN = new Dictionary<int, int>();
        private readonly double _w, _h;
        private double _lMargin, _tMargin, _rMargin, _cMargin, _x, _y, _lineWidth, _fontSizePt = 12, _fontSize;
        private readonly List<(string Key, Font F)> _fonts = new List<(string, Font)>();
        private readonly List<(string File, int Length1)> _fontFiles = new List<(string, int)>();
        private readonly Dictionary<string, int> _fontFileN = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _cmaps = new Dictionary<string, int>();
        private string _fontFamily = "", _fontStyle = "";
        private Font? _currentFont;
        private string _drawColor = "0 G", _fillColor = "0 g", _textColor = "0 g";
        private bool _colorFlag;
        private readonly List<KeyValuePair<string, string>> _metadata = new List<KeyValuePair<string, string>>();

        // ---- PDF/A-3 state
        private string? _attName;
        private byte[] _attData = Array.Empty<byte>();
        private string _attDesc = "", _attRel = "Alternative", _fxLevel = "";
        private const string FxDocType = "INVOICE";
        private readonly DateTimeOffset _time;
        private string _lang = "de-DE";
        private int _objOi, _objFs, _objXmp;
        public Action<PdfA3Writer>? Footer;
        /// <summary>set when Enc() had to simplify or replace a character</summary>
        public bool Replaced;

        public PdfA3Writer(DateTimeOffset? time = null)
        {
            _time = (time ?? DateTimeOffset.UtcNow).ToUniversalTime();
            _w = 595.28 / K; _h = 841.89 / K;
            var margin = 28.35 / K;
            SetMargins(margin, margin);
            _cMargin = margin / 10;
            _lineWidth = .567 / K;
            SetMeta("Producer", "FPDF 1.9");
            AddFont("WS", "", "Vera.json");
            AddFont("WS", "B", "VeraBd.json");
            SetMeta("Creator", PRODUCER);
            SetMeta("Producer", PRODUCER);
        }

        private void SetMeta(string k, string v)
        {
            var i = _metadata.FindIndex(p => p.Key == k);
            if (i >= 0) _metadata[i] = new KeyValuePair<string, string>(k, v); else _metadata.Add(new KeyValuePair<string, string>(k, v));
        }
        private string? Meta(string k) { var i = _metadata.FindIndex(p => p.Key == k); return i >= 0 ? _metadata[i].Value : null; }

        public void SetTitle(string s) => SetMeta("Title", s);
        public void SetAuthor(string s) => SetMeta("Author", s);
        public void SetSubject(string s) => SetMeta("Subject", s);
        public void SetLang(string lang) => _lang = lang;

        public void Attach(string name, byte[] data, string description, string level, string relationship = "Alternative")
        {
            _attName = name; _attData = data; _attDesc = description; _fxLevel = level; _attRel = relationship;
        }

        public void SetMargins(double left, double top, double? right = null) { _lMargin = left; _tMargin = top; _rMargin = right ?? left; }

        private static string F2(double v) => v.ToString("F2", Inv);
        private static string F3(double v) => v.ToString("F3", Inv);

        // ------------------------------------------------------------------ fonts
        private void AddFont(string family, string style, string file)
        {
            family = family.ToLowerInvariant(); style = style.ToUpperInvariant();
            var key = family + style;
            using var doc = JsonDocument.Parse(RulePack.ReadResource("fonts/" + file));
            var r = doc.RootElement;
            var f = new Font { I = _fonts.Count + 1, Type = r.GetProperty("type").GetString()!, Name = r.GetProperty("name").GetString()! };
            int c = 0;
            foreach (var w in r.GetProperty("cw").EnumerateArray()) f.Cw[c++] = w.GetInt32();
            foreach (var p in r.GetProperty("desc").EnumerateObject()) f.Desc.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));
            if (r.TryGetProperty("enc", out var enc)) f.Enc = enc.GetString()!.ToLowerInvariant();
            if (r.TryGetProperty("uv", out var uv))
                foreach (var p in uv.EnumerateObject())
                {
                    var code = int.Parse(p.Name, Inv);
                    if (p.Value.ValueKind == JsonValueKind.Array) f.Uv.Add((code, p.Value[0].GetInt32(), p.Value[1].GetInt32(), true));
                    else f.Uv.Add((code, p.Value.GetInt32(), 0, false));
                }
            f.Subsetted = r.TryGetProperty("subsetted", out var ss) && ss.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("file", out var ff) && ff.GetString() is string fn && fn != "")
            {
                f.File = fn;
                f.OriginalSize = r.GetProperty("originalsize").GetInt32();
                _fontFiles.Add((fn, f.OriginalSize));
            }
            _fonts.Add((key, f));
        }

        public void SetFont(string family, string style = "", double size = 0)
        {
            family = family == "" ? _fontFamily : family.ToLowerInvariant();
            style = style.ToUpperInvariant();
            if (size == 0) size = _fontSizePt;
            if (_fontFamily == family && _fontStyle == style && _fontSizePt == size) return;
            var key = family + style;
            var f = _fonts.First(p => p.Key == key).F;
            _fontFamily = family; _fontStyle = style; _fontSizePt = size; _fontSize = size / K; _currentFont = f;
            if (_page > 0) Out($"BT /F{f.I} {F2(_fontSizePt)} Tf ET");
        }

        public double GetStringWidth(string s)
        {
            var cw = _currentFont!.Cw;
            double w = 0;
            foreach (var ch in s) w += cw[ch & 0xFF];
            return w * _fontSize / 1000;
        }

        // ------------------------------------------------------------------ drawing
        public int PageNo() => _page;

        public void SetDrawColor(int r)
        {
            _drawColor = F3(r / 255.0) + " G";
            if (_page > 0) Out(_drawColor);
        }

        public void SetTextColor(int r)
        {
            _textColor = F3(r / 255.0) + " g";
            _colorFlag = _fillColor != _textColor;
        }

        public void SetLineWidth(double width)
        {
            _lineWidth = width;
            if (_page > 0) Out(F2(width * K) + " w");
        }

        public void Line(double x1, double y1, double x2, double y2)
            => Out($"{F2(x1 * K)} {F2((_h - y1) * K)} m {F2(x2 * K)} {F2((_h - y2) * K)} l S");

        public double GetY() => _y;
        public void SetX(double x) => _x = x >= 0 ? x : _w + x;
        public void SetY(double y, bool resetX = true)
        {
            _y = y >= 0 ? y : _h + y;
            if (resetX) _x = _lMargin;
        }
        public void SetXY(double x, double y) { SetX(x); SetY(y, false); }

        /// <summary>FPDF Cell with border 0, no fill, no link (auto page break is off in this SDK).</summary>
        public void Cell(double w, double h, string txt, int ln = 0, string align = "")
        {
            if (w == 0) w = _w - _rMargin - _x;
            var s = "";
            if (txt != "")
            {
                double dx;
                if (align == "R") dx = w - _cMargin - GetStringWidth(txt);
                else if (align == "C") dx = (w - GetStringWidth(txt)) / 2;
                else dx = _cMargin;
                if (_colorFlag) s += "q " + _textColor + " ";
                s += $"BT {F2((_x + dx) * K)} {F2((_h - (_y + .5 * h + .3 * _fontSize)) * K)} Td ({Escape(txt)}) Tj ET";
                if (_colorFlag) s += " Q";
            }
            if (s != "") Out(s);
            if (ln > 0) { _y += h; if (ln == 1) _x = _lMargin; }
            else _x += w;
        }

        public void AddPage()
        {
            var family = _fontFamily; var style = _fontStyle; var fontsize = _fontSizePt;
            var lw = _lineWidth; var dc = _drawColor; var fc = _fillColor; var tc = _textColor; var cf = _colorFlag;
            if (_page > 0)
            {
                Footer?.Invoke(this);
                _state = 1;
            }
            _page++;
            _pages.Add(new StringBuilder());
            _state = 2;
            _x = _lMargin; _y = _tMargin;
            _fontFamily = "";
            Out("2 J");
            _lineWidth = lw;
            Out(F2(lw * K) + " w");
            if (family != "") SetFont(family, style, fontsize);
            _drawColor = dc; if (dc != "0 G") Out(dc);
            _fillColor = fc; if (fc != "0 g") Out(fc);
            _textColor = tc; _colorFlag = cf;
            if (_lineWidth != lw) { _lineWidth = lw; Out(F2(lw * K) + " w"); }
            if (family != "") SetFont(family, style, fontsize);
            if (_drawColor != dc) { _drawColor = dc; Out(dc); }
            if (_fillColor != fc) { _fillColor = fc; Out(fc); }
            _textColor = tc; _colorFlag = cf;
        }

        private void Out(string s)
        {
            if (_state != 2) throw new InvalidOperationException("PDF writer: no open page");
            _pages[_page].Append(s).Append('\n');
        }

        // ------------------------------------------------------------------ text encoding
        private static readonly Dictionary<int, byte> Cp1252High = new Dictionary<int, byte>
        {
            [0x20AC] = 0x80, [0x201A] = 0x82, [0x0192] = 0x83, [0x201E] = 0x84, [0x2026] = 0x85, [0x2020] = 0x86, [0x2021] = 0x87,
            [0x02C6] = 0x88, [0x2030] = 0x89, [0x0160] = 0x8A, [0x2039] = 0x8B, [0x0152] = 0x8C, [0x017D] = 0x8E,
            [0x2018] = 0x91, [0x2019] = 0x92, [0x201C] = 0x93, [0x201D] = 0x94, [0x2022] = 0x95, [0x2013] = 0x96, [0x2014] = 0x97,
            [0x02DC] = 0x98, [0x2122] = 0x99, [0x0161] = 0x9A, [0x203A] = 0x9B, [0x0153] = 0x9C, [0x017E] = 0x9E, [0x0178] = 0x9F,
        };
        private static readonly Dictionary<string, string> Simplify = new Dictionary<string, string>
        {
            ["ł"] = "l", ["Ł"] = "L", ["đ"] = "d", ["Đ"] = "D", ["ı"] = "i", ["ħ"] = "h", ["Ħ"] = "H", ["ŧ"] = "t", ["€"] = "\x80",
        };

        private static string? To1252(string ch)
        {
            if (ch.Length == 1 && ch[0] < 0x80)
            {
                int o = ch[0];
                return (o >= 32 && o < 127) || ch == "\n" ? ch : (o < 32 ? " " : null);
            }
            if (ch.Length == 1)
            {
                int o = ch[0];
                if (o >= 0xA0 && o <= 0xFF) return ch;
                if (Cp1252High.TryGetValue(o, out var b)) return ((char)b).ToString();
                return null;
            }
            return null; // more than one code point / astral: never one Windows-1252 byte
        }

        /// <summary>Text -> the fonts' Windows-1252 encoding (one char per byte). Characters outside it are simplified (Ł -> L) or replaced by '?'.</summary>
        public string Enc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s!.Replace("\u2212", "-").Replace("\u2010", "-").Replace("\u2011", "-").Replace("\u00A0", " ").Replace("\u202F", " ").Replace("\t", " ");
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var ch = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? s.Substring(i++, 2) : s[i].ToString();
                var b = To1252(ch);
                if (b == null)
                {
                    Replaced = true;
                    if (Simplify.TryGetValue(ch, out var m)) b = m;
                    else
                    {
                        string d;
                        try { d = new string(ch.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()); }
                        catch (ArgumentException) { d = ""; }
                        b = d != "" && d != ch ? (To1252(d) ?? "?") : "?";
                    }
                }
                sb.Append(b);
            }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (s.IndexOf('(') < 0 && s.IndexOf(')') < 0 && s.IndexOf('\\') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "\\r");
        }

        /// <summary>FPDF _textstring for a Unicode string: ASCII as is, otherwise UTF-16BE with BOM (as bytes in a Latin-1 string).</summary>
        private static string TextString(string s)
        {
            if (s.Any(c => c > 127))
            {
                var be = Encoding.BigEndianUnicode.GetBytes(s);
                s = "\u00FE\u00FF" + Bytes.Latin1(be);
            }
            return "(" + Escape(s) + ")";
        }

        // ------------------------------------------------------------------ document output
        private void Put(string s) => _buffer.Append(s).Append('\n');
        private void NewObj(int? n = null)
        {
            int k = n ?? ++_n;
            _offsets[k] = _buffer.Length;
            Put(k + " 0 obj");
        }
        private void PutStream(string data) { Put("stream"); Put(data); Put("endstream"); }
        private void PutStreamObject(string data)
        {
            var z = Bytes.Latin1(Bytes.Deflate(Bytes.FromLatin1(data)));
            NewObj();
            Put("<</Filter /FlateDecode /Length " + z.Length + ">>");
            PutStream(z);
            Put("endobj");
        }
        private int StreamObj(string dict, byte[] data, bool compress = true)
        {
            if (compress) { data = Bytes.Deflate(data); dict += " /Filter /FlateDecode"; }
            NewObj();
            Put($"<<{dict} /Length {data.Length}>>");
            PutStream(Bytes.Latin1(data));
            Put("endobj");
            return _n;
        }

        private string PdfDate() => "D:" + _time.UtcDateTime.ToString("yyyyMMddHHmmss", Inv) + "+00'00'";

        public byte[] Output()
        {
            if (_page == 0) AddPage();
            Footer?.Invoke(this);
            _state = 1;
            // header
            Put("%PDF-1.7");
            Put("%\u00E2\u00E3\u00CF\u00D3");
            PutPages();
            PutResources();
            NewObj(); Put("<<");
            SetMeta("CreationDate", PdfDate()); SetMeta("ModDate", PdfDate());
            foreach (var kv in _metadata) Put("/" + kv.Key + " " + TextString(kv.Value));
            Put(">>"); Put("endobj");
            NewObj(); Put("<<");
            PutCatalog();
            Put(">>"); Put("endobj");
            var offset = _buffer.Length;
            Put("xref");
            Put("0 " + (_n + 1));
            Put("0000000000 65535 f ");
            for (int i = 1; i <= _n; i++) Put(_offsets[i].ToString("D10", Inv) + " 00000 n ");
            Put("trailer"); Put("<<");
            Put("/Size " + (_n + 1)); Put("/Root " + _n + " 0 R"); Put("/Info " + (_n - 1) + " 0 R");
            var id = Md5Hex(Bytes.FromLatin1(_buffer.ToString()));
            Put($"/ID [<{id}> <{id}>]");
            Put(">>"); Put("startxref"); Put(offset.ToString(Inv)); Put("%%EOF");
            _state = 3;
            return Bytes.FromLatin1(_buffer.ToString());
        }

        private static string Md5Hex(byte[] b)
        {
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        private void PutPages()
        {
            int nb = _page, n = _n;
            for (int i = 1; i <= nb; i++) { _pageN[i] = ++n; n++; }
            for (int i = 1; i <= nb; i++)
            {
                NewObj();
                Put("<</Type /Page"); Put("/Parent 1 0 R"); Put("/Resources 2 0 R");
                Put("/Contents " + (_n + 1) + " 0 R>>"); Put("endobj");
                PutStreamObject(_pages[i].ToString());
            }
            NewObj(1);
            Put("<</Type /Pages");
            var kids = "/Kids [";
            for (int i = 1; i <= nb; i++) kids += _pageN[i] + " 0 R ";
            Put(kids + "]");
            Put("/Count " + nb);
            Put($"/MediaBox [0 0 {F2(_w * K)} {F2(_h * K)}]");
            Put(">>"); Put("endobj");
        }

        private static string Cmap(List<(int Code, int Start, int Count, bool Range)> uv)
        {
            var ranges = new StringBuilder(); var chars = new StringBuilder(); int nbr = 0, nbc = 0;
            foreach (var u in uv)
            {
                if (u.Range) { ranges.Append($"<{u.Code:X2}> <{u.Code + u.Count - 1:X2}> <{u.Start:X4}>\n"); nbr++; }
                else { chars.Append($"<{u.Code:X2}> <{u.Start:X4}>\n"); nbc++; }
            }
            var s = new StringBuilder();
            s.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo\n<</Registry (Adobe)\n/Ordering (UCS)\n/Supplement 0\n>> def\n");
            s.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
            if (nbr > 0) s.Append(nbr).Append(" beginbfrange\n").Append(ranges).Append("endbfrange\n");
            if (nbc > 0) s.Append(nbc).Append(" beginbfchar\n").Append(chars).Append("endbfchar\n");
            s.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
            return s.ToString();
        }

        private void PutResources()
        {
            foreach (var (file, length1) in _fontFiles)
            {
                NewObj();
                _fontFileN[file] = _n;
                var font = Bytes.Latin1(RulePack.ReadResource("fonts/" + file));
                Put("<</Length " + font.Length);
                if (file.EndsWith(".z", StringComparison.Ordinal)) Put("/Filter /FlateDecode");
                Put("/Length1 " + length1);
                Put(">>");
                PutStream(font);
                Put("endobj");
            }
            foreach (var (_, font) in _fonts)
            {
                string cmapkey = font.Enc ?? font.Name;
                if (font.Uv.Count > 0 && !_cmaps.ContainsKey(cmapkey))
                {
                    PutStreamObject(Cmap(font.Uv));
                    _cmaps[cmapkey] = _n;
                }
                font.N = _n + 1;
                var name = font.Subsetted ? "AAAAAA+" + font.Name : font.Name;
                NewObj();
                Put("<</Type /Font"); Put("/BaseFont /" + name); Put("/Subtype /" + font.Type);
                Put("/FirstChar 32 /LastChar 255");
                Put("/Widths " + (_n + 1) + " 0 R"); Put("/FontDescriptor " + (_n + 2) + " 0 R");
                Put("/Encoding /WinAnsiEncoding");
                if (font.Uv.Count > 0) Put("/ToUnicode " + _cmaps[cmapkey] + " 0 R");
                Put(">>"); Put("endobj");
                NewObj();
                var sb = new StringBuilder("[");
                for (int i = 32; i <= 255; i++) sb.Append(font.Cw[i]).Append(' ');
                Put(sb + "]"); Put("endobj");
                NewObj();
                var d = new StringBuilder("<</Type /FontDescriptor /FontName /" + name);
                foreach (var (k, v) in font.Desc) d.Append(" /").Append(k).Append(' ').Append(v);
                if (font.File != "") d.Append(" /FontFile2 ").Append(_fontFileN[font.File]).Append(" 0 R");
                Put(d + ">>"); Put("endobj");
            }
            NewObj(2);
            Put("<<");
            Put("/ProcSet [/PDF /Text /ImageB /ImageC /ImageI]");
            Put("/Font <<");
            foreach (var (_, font) in _fonts) Put("/F" + font.I + " " + font.N + " 0 R");
            Put(">>"); Put("/XObject <<"); Put(">>");
            Put(">>"); Put("endobj");
            // PDF/A-3: output intent, embedded file, XMP
            var iccN = StreamObj("/N 3", RulePack.ReadResource("sRGB2014.icc"));
            NewObj();
            Put($"<</Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB IEC61966-2.1) /RegistryName (http://www.color.org) /Info (sRGB IEC61966-2.1) /DestOutputProfile {iccN} 0 R>>");
            Put("endobj");
            _objOi = _n;
            if (_attName != null)
            {
                var d = PdfDate();
                var ef = StreamObj("/Type /EmbeddedFile /Subtype /text#2Fxml /Params <</ModDate " + TextString(d) + " /Size " + _attData.Length
                    + " /CheckSum <" + Md5Hex(_attData) + ">>>", _attData);
                NewObj();
                var fn = TextString(_attName);
                Put($"<</Type /Filespec /F {fn} /UF {fn} /Desc {TextString(_attDesc)} /AFRelationship /{_attRel} /EF <</F {ef} 0 R /UF {ef} 0 R>>>>");
                Put("endobj");
                _objFs = _n;
            }
            _objXmp = StreamObj("/Type /Metadata /Subtype /XML", Bytes.Utf8.GetBytes(Xmp()), false);
        }

        private void PutCatalog()
        {
            Put("/Type /Catalog"); Put("/Pages 1 0 R");
            Put("/Metadata " + _objXmp + " 0 R");
            Put("/OutputIntents [" + _objOi + " 0 R]");
            Put("/Lang " + TextString(_lang));
            Put("/MarkInfo <</Marked false>>");
            if (_objFs > 0)
            {
                Put("/Names <</EmbeddedFiles <</Names [" + TextString(_attName!) + " " + _objFs + " 0 R]>>>>");
                Put("/AF [" + _objFs + " 0 R]");
                Put("/PageMode /UseAttachments");
            }
        }

        private static string X(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

        private string Xmp()
        {
            var iso = _time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + "+00:00";
            var t = X(Meta("Title") ?? ""); var a = X(Meta("Author") ?? ""); var s = X(Meta("Subject") ?? ""); var p = X(PRODUCER);
            var fx = "";
            if (_attName != null)
            {
                fx = "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n"
                    + "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">\n"
                    + "<pdfaSchema:schema>Factur-X PDFA Extension Schema</pdfaSchema:schema>\n"
                    + "<pdfaSchema:namespaceURI>urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#</pdfaSchema:namespaceURI>\n"
                    + "<pdfaSchema:prefix>fx</pdfaSchema:prefix>\n"
                    + "<pdfaSchema:property><rdf:Seq>\n"
                    + "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>DocumentFileName</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The name of the embedded XML document</pdfaProperty:description></rdf:li>\n"
                    + "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>DocumentType</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The type of the hybrid document in capital letters, e.g. INVOICE or ORDER</pdfaProperty:description></rdf:li>\n"
                    + "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>Version</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The actual version of the standard applying to the embedded XML document</pdfaProperty:description></rdf:li>\n"
                    + "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>ConformanceLevel</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The conformance level of the embedded XML document</pdfaProperty:description></rdf:li>\n"
                    + "</rdf:Seq></pdfaSchema:property>\n"
                    + "</rdf:li></rdf:Bag></pdfaExtension:schemas>\n"
                    + "</rdf:Description>\n"
                    + "<rdf:Description rdf:about=\"\" xmlns:fx=\"urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#\"><fx:DocumentType>" + FxDocType + "</fx:DocumentType><fx:DocumentFileName>"
                    + X(_attName) + "</fx:DocumentFileName><fx:Version>1.0</fx:Version><fx:ConformanceLevel>" + X(_fxLevel) + "</fx:ConformanceLevel></rdf:Description>\n";
            }
            return "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
                + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n"
                + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n"
                + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"><pdfaid:part>3</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>\n"
                + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n"
                + $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{t}</rdf:li></rdf:Alt></dc:title>\n"
                + $"<dc:creator><rdf:Seq><rdf:li>{a}</rdf:li></rdf:Seq></dc:creator>\n"
                + $"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{s}</rdf:li></rdf:Alt></dc:description>\n"
                + "</rdf:Description>\n"
                + $"<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\"><pdf:Producer>{p}</pdf:Producer></rdf:Description>\n"
                + $"<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><xmp:CreatorTool>{p}</xmp:CreatorTool><xmp:CreateDate>{iso}</xmp:CreateDate><xmp:ModifyDate>{iso}</xmp:ModifyDate><xmp:MetadataDate>{iso}</xmp:MetadataDate></rdf:Description>\n"
                + fx + "</rdf:RDF>\n"
                + "</x:xmpmeta>\n"
                + "<?xpacket end=\"w\"?>";
        }
    }
}
