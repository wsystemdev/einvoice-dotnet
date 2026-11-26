using System.Collections.Generic;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Generator
{
    internal sealed class BuiltXml
    {
        public string Format = "";
        public string Syntax = "";
        public string Xml = "";
        public Dictionary<string, string> Totals = new Dictionary<string, string>();
        public Money Model = new Money();
        public bool XRechnung;
        public CiiGenerator.FormatInfo? Pdf;
    }

    internal static class XmlBuilder
    {
        public static readonly string[] CII_FORMATS = { "cii", "xrechnung-cii", "zugferd", "factur-x", "zugferd-xrechnung" };

        public static BuiltXml Build(object? invoice)
        {
            var fmt = CiiGenerator.NormFormat(Py.G(invoice, "format"));
            if (CiiGenerator.FORMATS.TryGetValue(fmt, out var fi))
            {
                var (xml, totals, m) = CiiGenerator.Build(invoice, fmt);
                return new BuiltXml { Format = fmt, Syntax = "UN/CEFACT CII D16B", Xml = xml, Totals = totals, Model = m,
                    XRechnung = fi.Gid == Codes.XRECHNUNG_ID, Pdf = fi.IsPdf ? fi : null };
            }
            var (ux, ut, um) = UblGenerator.Build(invoice);
            var f = Py.Lower(Py.T(Py.G(invoice, "format")) ? Py.G(invoice, "format") : "en16931");
            return new BuiltXml { Format = f, Syntax = "UBL 2.1", Xml = ux, Totals = ut, Model = um, XRechnung = f == "xrechnung" };
        }
    }
}
