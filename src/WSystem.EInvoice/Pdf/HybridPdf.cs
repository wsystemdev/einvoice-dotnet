using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Pdf
{
    /// <summary>
    /// ZUGFeRD 2.3 / Factur-X 1.07 hybrid invoice: a readable A4 invoice (bilingual German / English labels, the same
    /// content as the W System API's PDF) as PDF/A-3b with the CII XML embedded as factur-x.xml (xrechnung.xml for the
    /// XRECHNUNG profile).
    /// </summary>
    internal sealed class HybridPdf
    {
        public static readonly Dictionary<string, string> UNIT = new Dictionary<string, string>
        {
            ["C62"] = "Stk / pcs", ["HUR"] = "Std / h", ["DAY"] = "Tag / day", ["H87"] = "Stk / pcs", ["XPK"] = "Pck / pack", ["KGM"] = "kg", ["MTR"] = "m",
            ["LTR"] = "l", ["MON"] = "Monat / month", ["ANN"] = "Jahr / year", ["MIN"] = "min", ["KMT"] = "km", ["MTK"] = "m²", ["LS"] = "pauschal / lump sum",
            ["WEE"] = "Woche / week", ["SET"] = "Set",
        };
        public static readonly Dictionary<string, string> TITLE = new Dictionary<string, string>
        {
            ["380"] = "Rechnung / Invoice", ["381"] = "Gutschrift / Credit note", ["384"] = "Rechnungskorrektur / Corrected invoice",
            ["389"] = "Gutschrift (Selbstfakturierung) / Self-billed invoice", ["326"] = "Teilrechnung / Partial invoice",
            ["875"] = "Abschlagsrechnung / Partial construction invoice", ["877"] = "Schlussrechnung / Final construction invoice",
            ["386"] = "Vorauszahlungsrechnung / Prepayment invoice",
        };
        public static readonly Dictionary<string, string> AC_REASON = new Dictionary<string, string>
        {
            ["95"] = "Rabatt / Discount", ["100"] = "Sondervereinbarung / Special agreement", ["102"] = "Langfristige Vereinbarung / Fixed long term",
            ["103"] = "Befristet / Temporary", ["104"] = "Standard", ["41"] = "Bonus", ["42"] = "Bonus", ["60"] = "Herstellerrabatt / Manufacturer discount",
            ["64"] = "Sondervereinbarung / Special agreement", ["65"] = "Rabatt Produktionsfehler / Production error discount",
            ["66"] = "Neueröffnungsrabatt / New outlet discount", ["67"] = "Musterrabatt / Sample discount", ["68"] = "Auslaufrabatt / End-of-range discount",
            ["88"] = "Materialzu-/abschlag / Material surcharge", ["FC"] = "Fracht / Freight", ["ABL"] = "Verpackung / Packaging",
            ["AEO"] = "Sammlung und Recycling / Collection and recycling", ["ADR"] = "Sonstige Leistungen / Other services",
            ["ZZZ"] = "Vereinbart / Mutually defined", ["SH"] = "Sonderbehandlung / Special handling", ["DL"] = "Lieferung / Delivery",
        };
        public static readonly Dictionary<string, string> CAT = new Dictionary<string, string>
        {
            ["S"] = "", ["Z"] = "Nullsatz / zero rated", ["E"] = "steuerfrei / exempt", ["AE"] = "Steuerschuldnerschaft des Leistungsempfängers / reverse charge",
            ["K"] = "innergemeinschaftliche Lieferung / intra-Community supply", ["G"] = "Ausfuhr / export", ["O"] = "nicht steuerbar / not subject to VAT",
            ["L"] = "IGIC", ["M"] = "IPSI",
        };

        private const double L = 18.0, W = 174.0, BOTTOM = 277.0;

        private PdfA3Writer _pdf = null!;
        private bool _de;

        private struct Part
        {
            public string Text; public bool Bold; public double Size; public bool Grey;
            public Part(string t, bool b, double s, bool g) { Text = t; Bold = b; Size = s; Grey = g; }
        }

        /// <returns>the PDF and whether characters outside Windows-1252 were simplified in the visual part</returns>
        public static (byte[] Pdf, bool Replaced) Build(object? j, Money m, string xml, string fmt, string fileName, string level, DateTimeOffset? time = null)
        {
            var label = level != "XRECHNUNG" ? "ZUGFeRD 2.3 / Factur-X 1.07, Profil " + level : "ZUGFeRD 2.3, Profil XRECHNUNG";
            var h = new HybridPdf();
            var pdf = h.Render(j, m, label, fileName, xml, level, time);
            return (pdf, h._pdf.Replaced);
        }

        private string Money(PyDecimal v, int places = 2)
        {
            var neg = v.IsNegative;
            var r = v.Round(places).ToString().TrimStart('-');
            var sp = r.Split(new[] { '.' }, 2);
            var intp = sp[0]; var frac = sp.Length > 1 ? sp[1] : "";
            var rev = new string(intp.Reverse().ToArray());
            var chunks = Enumerable.Range(0, (rev.Length + 2) / 3).Select(i => rev.Substring(i * 3, Math.Min(3, rev.Length - i * 3)));
            intp = new string(string.Join(_de ? "." : ",", chunks).Reverse().ToArray());
            var o = intp + (places > 0 ? (_de ? "," : ".") + frac : "");
            return (neg ? "-" : "") + o;
        }

        private string Qty(PyDecimal v) { var s = v.Num(); return _de ? s.Replace(".", ",") : s; }

        private string D(object? s)
        {
            if (!Py.T(s)) return "";
            var p = Py.Str(s).Split('-');
            if (p.Length != 3) return Py.Str(s);
            return _de ? $"{p[2]}.{p[1]}.{p[0]}" : $"{p[0]}-{p[1]}-{p[2]}";
        }

        private static string S(object? v) => v == null ? "" : Py.Str(v);

        private void Font(bool bold, double size) => _pdf.SetFont("WS", bold ? "B" : "", size);

        private static readonly Regex Spaces = new Regex(" +", RegexOptions.CultureInvariant);

        private List<string> Wrap(string text, double w)
        {
            var o = new List<string>();
            foreach (var para in _pdf.Enc(text).Split('\n'))
            {
                var line = "";
                foreach (var word0 in Spaces.Split(para))
                {
                    var word = word0;
                    var tr = line == "" ? word : line + " " + word;
                    if (_pdf.GetStringWidth(tr) <= w) { line = tr; continue; }
                    if (line != "") o.Add(line);
                    while (_pdf.GetStringWidth(word) > w && word.Length > 1)
                    {
                        int k = word.Length;
                        while (k > 1 && _pdf.GetStringWidth(word.Substring(0, k)) > w) k--;
                        o.Add(word.Substring(0, k));
                        word = word.Substring(k);
                    }
                    line = word;
                }
                o.Add(line);
            }
            return o;
        }

        private double Block(double x, double y, double w, IEnumerable<Part> parts, string align = "L", bool dry = false)
        {
            double h = 0.0;
            foreach (var p in parts)
            {
                Font(p.Bold, p.Size);
                var lh = p.Size * 0.3528 * 1.3;
                foreach (var ln in Wrap(p.Text, w))
                {
                    if (!dry)
                    {
                        _pdf.SetTextColor(p.Grey ? 68 : 0);
                        _pdf.SetXY(x, y + h);
                        _pdf.Cell(w, lh, ln, 0, align);
                    }
                    h += lh;
                }
            }
            _pdf.SetTextColor(0);
            return h;
        }

        private void Ensure(double h, Action? onNewPage = null)
        {
            if (_pdf.GetY() + h > BOTTOM)
            {
                _pdf.AddPage();
                _pdf.SetY(16);
                onNewPage?.Invoke();
            }
        }

        private double Row(List<(double W, List<Part> Parts, string Align)> cells, Action? onNewPage = null, double pad = 1.0)
        {
            double h = 0.0;
            foreach (var c in cells) h = Math.Max(h, Block(0, 0, c.W - 2 * pad, c.Parts, c.Align, true));
            h += 2 * pad;
            Ensure(h, onNewPage);
            var y = _pdf.GetY();
            var x = L;
            foreach (var c in cells)
            {
                Block(x + pad, y + pad, c.W - 2 * pad, c.Parts, c.Align);
                x += c.W;
            }
            _pdf.SetY(y + h);
            return h;
        }

        private void Rule(double width, int grey, double x = L, double w = W)
        {
            _pdf.SetDrawColor(grey);
            _pdf.SetLineWidth(width);
            var y = _pdf.GetY();
            _pdf.Line(x, y, x + w, y);
        }

        private static Part P(string t, bool b, double s, bool g) => new Part(t, b, s, g);

        private byte[] Render(object? j, Money m, string label, string fileName, string xml, string level, DateTimeOffset? time)
        {
            var seller = Py.IsDict(Py.G(j, "seller")) ? Py.G(j, "seller") : new Dictionary<string, object?>();
            var buyer = Py.IsDict(Py.G(j, "buyer")) ? Py.G(j, "buyer") : new Dictionary<string, object?>();
            var pay = Py.T(Py.G(j, "payment")) && Py.IsDict(Py.G(j, "payment")) ? Py.G(j, "payment") : new Dictionary<string, object?>();
            _de = new[] { "DE", "AT", "CH", "LU", "LI" }.Contains(Py.Upper(S(Py.G(seller, "country") ?? "")));
            var cur = m.Cur;
            var tc = Py.Str(Py.T(Py.G(j, "type_code")) ? Py.G(j, "type_code") : (m.IsCn ? "381" : "380"));
            var title = TITLE.TryGetValue(tc, out var tt) ? tt : "Rechnung / Invoice";
            var tparts = title.Split(new[] { " / " }, StringSplitOptions.None);
            var num = Py.Str(Py.G(j, "invoice_number"));

            _pdf = new PdfA3Writer(time);
            var pdf = _pdf;
            pdf.SetTitle(tparts[tparts.Length - 1] + " " + num);
            pdf.SetAuthor(S(Py.G(seller, "name") ?? ""));
            pdf.SetSubject($"{label} e-invoice {num}");
            pdf.SetMargins(L, 16, L);
            pdf.Attach(fileName, Bytes.Utf8.GetBytes(xml), "Factur-X / ZUGFeRD e-invoice (UN/CEFACT CII)", level);
            var footerText = SubstrCodepoints(S(Py.G(seller, "name") ?? "") + " · " + tparts[0] + " " + num, 120);
            pdf.Footer = p =>
            {
                p.SetFont("WS", "", 7);
                p.SetTextColor(102);
                p.SetXY(L, 287 - 3);
                p.Cell(W / 2, 4, p.Enc(footerText), 0, "L");
                p.SetXY(L + W / 2, 287 - 3);
                p.Cell(W / 2, 4, p.Enc("Seite / Page " + p.PageNo()), 0, "R");
                p.SetTextColor(0);
            };
            pdf.AddPage();
            pdf.SetY(16);

            List<Part> Addr(object? p)
            {
                var o = new List<Part> { P(S(Py.G(p, "name")), true, 8.5, false) };
                if (Py.T(Py.G(p, "trading_name"))) o.Add(P(Py.Str(Py.G(p, "trading_name")), false, 8.5, false));
                if (Py.T(Py.G(p, "street"))) o.Add(P(Py.Str(Py.G(p, "street")), false, 8.5, false));
                var pc = string.Join(" ", new[] { Py.G(p, "postal_code"), Py.G(p, "city") }.Where(Py.T).Select(Py.Str));
                if (pc != "") o.Add(P(pc, false, 8.5, false));
                var cc = Py.Upper(S(Py.G(p, "country") ?? ""));
                if (cc != "") o.Add(P(cc, false, 8.5, false));
                return o;
            }
            List<Part> Ids(object? p)
            {
                var o = new List<Part>();
                if (Py.T(Py.G(p, "vat_id"))) o.Add(P("USt-IdNr. / VAT ID: " + Py.Str(Py.G(p, "vat_id")), false, 7.2, true));
                if (Py.T(Py.G(p, "tax_number"))) o.Add(P("Steuernummer / Tax no.: " + Py.Str(Py.G(p, "tax_number")), false, 7.2, true));
                if (Py.T(Py.G(p, "company_id"))) o.Add(P("Register / Reg. no.: " + Py.Str(Py.G(p, "company_id")), false, 7.2, true));
                return o;
            }

            // header: seller | title
            var sellerParts = Addr(seller).Concat(Ids(seller)).ToList();
            var contact = new[] { Py.G(seller, "contact_name"), Py.G(seller, "phone"), Py.G(seller, "email") }.Where(Py.T).Select(Py.Str).ToList();
            if (contact.Count > 0) sellerParts.Add(P(string.Join(" · ", contact), false, 7.2, true));
            var y0 = pdf.GetY();
            var h1 = Block(L, y0, W * 0.55, sellerParts);
            var h2 = Block(L + W * 0.55, y0, W * 0.45, new[] { P(title, true, 15, false) }, "R");
            pdf.SetY(y0 + Math.Max(h1, h2) + 8);

            // buyer | meta
            var meta = new List<(string, string)> { ("Nummer / Number", num), ("Datum / Issue date", D(Py.G(j, "issue_date"))) };
            if (Py.T(Py.G(j, "due_date")) && !m.IsCn) meta.Add(("Fällig am / Due date", D(Py.G(j, "due_date"))));
            if (Py.T(Py.G(j, "delivery_date"))) meta.Add(("Leistungsdatum / Delivery date", D(Py.G(j, "delivery_date"))));
            if (Py.T(Py.G(j, "tax_point_date"))) meta.Add(("Steuerdatum / Tax point date", D(Py.G(j, "tax_point_date"))));
            if (Py.T(Py.G(j, "buyer_reference"))) meta.Add(("Käuferreferenz (Leitweg-ID) / Buyer ref.", Py.Str(Py.G(j, "buyer_reference"))));
            if (Py.T(Py.G(j, "order_reference"))) meta.Add(("Bestellung / Purchase order", Py.Str(Py.G(j, "order_reference"))));
            if (Py.T(Py.G(j, "preceding_invoice")))
                meta.Add(("Bezug auf Rechnung / Corrects invoice", Py.Str(Py.G(j, "preceding_invoice"))
                    + (Py.T(Py.G(j, "preceding_invoice_date")) ? " (" + D(Py.G(j, "preceding_invoice_date")) + ")" : "")));
            y0 = pdf.GetY();
            h1 = Block(L, y0, W * 0.55, new[] { P("Rechnungsempfänger / Bill to", false, 7.2, true) }.Concat(Addr(buyer)).Concat(Ids(buyer)));
            var yy = y0;
            foreach (var (k, v) in meta)
            {
                var a = Block(L + W * 0.55, yy, W * 0.25, new[] { P(k, false, 7.2, true) });
                var b = Block(L + W * 0.80, yy, W * 0.20, new[] { P(v, false, 8.5, false) }, "R");
                yy += Math.Max(a, b) + 0.7;
            }
            pdf.SetY(Math.Max(y0 + h1, yy) + 8);
            if (Py.T(Py.G(j, "note")))
                pdf.SetY(pdf.GetY() + Block(L, pdf.GetY(), W, new[] { P(Py.Str(Py.G(j, "note")), false, 8.5, false) }) + 4);

            // lines
            var cw = new[] { 0.06, 0.38, 0.09, 0.11, 0.12, 0.09, 0.15 }.Select(f => f * W).ToArray();
            void Head()
            {
                var hd = new[] { ("Pos.", "L"), ("Beschreibung / Description", "L"), ("Menge / Qty", "R"), ("Einheit / Unit", "L"), ("Preis / Unit price", "R"),
                    ("USt / VAT", "R"), ($"Betrag / Amount ({cur})", "R") };
                var cells = hd.Select((t, i) => (cw[i], new List<Part> { P(t.Item1, true, 7.2, false) }, t.Item2)).ToList();
                Row(cells);
                Rule(0.28, 34);
            }
            Head();
            foreach (var ln in m.Lines)
            {
                var dsc = new List<Part> { P(ln.Name, true, 8.5, false) };
                if (Py.T(ln.Desc)) dsc.Add(P(Py.Str(ln.Desc), false, 8.5, false));
                if (Py.T(ln.Sid)) dsc.Add(P("Art.-Nr. / Item: " + Py.Str(ln.Sid), false, 7, false));
                foreach (var a in ln.Acs)
                {
                    var lbl = AcReason(a);
                    dsc.Add(P((a.Charge ? "+" : "-") + " " + lbl + (a.Percent != null ? " " + Qty(a.Percent) + " %" : "") + ": " + Money(a.Amount), false, 7, false));
                }
                var rate = ln.Cat != "O" ? Qty(ln.Rate) + " %" : "–";
                var pplaces = Math.Max(2, Decimals(ln.Price));
                var unit = Py.Str(ln.Unit);
                Row(new List<(double, List<Part>, string)>
                {
                    (cw[0], new List<Part> { P(ln.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), false, 8.5, false) }, "L"),
                    (cw[1], dsc, "L"),
                    (cw[2], new List<Part> { P(Qty(ln.Q), false, 8.5, false) }, "R"),
                    (cw[3], new List<Part> { P(UNIT.TryGetValue(unit, out var u) ? u : unit, false, 8.5, false) }, "L"),
                    (cw[4], new List<Part> { P(Money(ln.Price, pplaces), false, 8.5, false) }, "R"),
                    (cw[5], new List<Part> { P(rate + (ln.Cat != "S" ? " " + ln.Cat : ""), false, 8.5, false) }, "R"),
                    (cw[6], new List<Part> { P(Money(ln.Net), false, 8.5, false) }, "R"),
                }, Head);
                Rule(0.09, 187);
            }
            pdf.SetY(pdf.GetY() + 4);

            // totals
            var tot = new List<(string, string, bool)> { ("Summe Positionen / Line total", Money(m.LineTotal), false) };
            foreach (var a in m.DocAcs) tot.Add((DocAcLabel(a), (a.Charge ? "" : "-") + Money(a.Amount), false));
            if (m.DocAcs.Count > 0) tot.Add(("Nettobetrag / Net amount", Money(m.Basis), false));
            foreach (var g in m.Vat)
            {
                var lbl = (g.Cat != "O" ? "USt / VAT " + Qty(g.Rate) + " %" : "USt / VAT") + " auf / on " + Money(g.Base);
                if (g.Cat != "S") lbl += $" ({g.Cat}: " + (CAT.TryGetValue(g.Cat, out var cd) ? cd : "") + ")";
                tot.Add((lbl, Money(g.Tax), false));
            }
            var grandIdx = tot.Count;
            tot.Add(($"Gesamtbetrag / Total ({cur})", Money(m.Incl), true));
            if (!m.Prepaid.IsZero)
            {
                tot.Add(("Bereits gezahlt / Prepaid", "-" + Money(m.Prepaid), false));
                tot.Add(($"Zahlbetrag / Amount due ({cur})", Money(m.Payable), true));
            }
            var tx = L + W * 0.25;
            for (int i = 0; i < tot.Count; i++)
            {
                var (k, v, bold) = tot[i];
                if (i == grandIdx) Rule(0.28, 34, tx, W * 0.75);
                Ensure(6);
                var y = pdf.GetY();
                var a = Block(tx, y + 0.5, W * 0.56 - 2, new[] { P(k, bold, 8.5, false) }, "R");
                var b = Block(tx + W * 0.56, y + 0.5, W * 0.19 - 1, new[] { P(v, bold, 8.5, false) }, "R");
                pdf.SetY(y + Math.Max(a, b) + 1);
            }
            pdf.SetY(pdf.GetY() + 6);
            foreach (var g in m.Vat)
            {
                if (Py.T(g.ExText))
                {
                    Ensure(5);
                    pdf.SetY(pdf.GetY() + Block(L, pdf.GetY(), W,
                        new[] { P($"{g.Cat}: " + Py.Str(g.ExText) + (Py.T(g.ExCode) ? " (" + Py.Str(g.ExCode) + ")" : ""), false, 7.2, true) }));
                }
            }
            var pl = new List<string>();
            if (Py.T(Py.G(pay, "iban")))
                pl.Add("IBAN: " + Py.Str(Py.G(pay, "iban")) + (Py.T(Py.G(pay, "bic")) ? " · BIC: " + Py.Str(Py.G(pay, "bic")) : "")
                    + (Py.T(Py.G(pay, "account_name")) ? " · Kontoinhaber / Account holder: " + Py.Str(Py.G(pay, "account_name")) : ""));
            if (Py.T(Py.G(pay, "reference"))) pl.Add("Verwendungszweck / Payment reference: " + Py.Str(Py.G(pay, "reference")));
            if (Py.T(Py.G(pay, "terms"))) pl.Add("Zahlungsbedingungen / Terms: " + Py.Str(Py.G(pay, "terms")));
            if (pl.Count > 0)
            {
                var parts = new List<Part> { P("Zahlung / Payment", true, 8.5, false) };
                foreach (var x in pl) parts.Add(P(x, false, 8.5, false));
                pdf.SetY(pdf.GetY() + 2);
                Ensure(Block(L, 0, W, parts, "L", true));
                pdf.SetY(pdf.GetY() + Block(L, pdf.GetY(), W, parts));
            }
            var note = $"Diese PDF-Datei ist eine hybride E-Rechnung ({label}). Die eingebettete Datei {fileName} enthält die strukturierten "
                + $"Rechnungsdaten (UN/CEFACT CII). / This PDF is a hybrid e-invoice ({label}); the embedded {fileName} carries the structured invoice data (UN/CEFACT CII).";
            pdf.SetY(pdf.GetY() + 6);
            Ensure(Block(L, 0, W, new[] { P(note, false, 7.2, true) }, "L", true));
            Block(L, pdf.GetY(), W, new[] { P(note, false, 7.2, true) });
            return pdf.Output();
        }

        private static string SubstrCodepoints(string s, int n)
        {
            int count = 0, i = 0;
            // count code points, not text elements
            while (i < s.Length && count < n)
            {
                i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                count++;
            }
            return s.Substring(0, i);
        }

        private string AcReason(Ac a)
        {
            if (Py.T(a.Reason)) return Py.Str(a.Reason);
            var code = Py.Str(a.ReasonCode ?? "");
            return AC_REASON.TryGetValue(code, out var r) ? r : code;
        }

        private string DocAcLabel(Ac a)
        {
            var kind = a.Charge ? "Zuschlag / Charge" : "Nachlass / Allowance";
            var reason = AcReason(a);
            var lbl = reason != "" && reason != kind ? $"{kind}: {reason}" : kind;
            if (a.Percent != null) lbl += " (" + Qty(a.Percent) + " %)";
            if (a.Cat != "O") lbl += " [" + Qty(a.Rate) + " %]";
            return lbl;
        }

        private static int Decimals(PyDecimal v)
        {
            var s = v.Num();
            var p = s.IndexOf('.');
            return p < 0 ? 0 : s.Length - p - 1;
        }
    }
}
