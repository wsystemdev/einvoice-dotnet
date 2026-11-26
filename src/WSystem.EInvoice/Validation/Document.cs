using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Pdf;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Validation
{
    /// <summary>A parsed e-invoice: the XML (taken from the PDF for hybrids), its DOM and what it claims to be.</summary>
    internal sealed class Document
    {
        public static readonly string[] PDF_XML_NAMES = { "factur-x.xml", "zugferd-invoice.xml", "xrechnung.xml", "zugferd_invoice.xml", "order-x.xml" };
        public const int DEFAULT_MAX_BYTES = 5 * 1024 * 1024;

        public byte[] Xml = Array.Empty<byte>();
        public XmlDocument Dom = new XmlDocument();
        public string Container = "xml";
        public string? EmbeddedFile;
        public string Syntax = "", DocumentType = "", CustomizationId = "", Profile = "";
        public List<string> Notes = new List<string>();

        public static Document Load(byte[] data, int maxBytes = DEFAULT_MAX_BYTES)
        {
            var (xml, container, embedded) = ExtractXml(data, maxBytes);
            var dom = Parse(xml);
            var d = Detect(dom);
            d.Xml = xml; d.Container = container; d.EmbeddedFile = embedded;
            return d;
        }

        public static (byte[] Xml, string Container, string? Embedded) ExtractXml(byte[] data, int maxBytes = DEFAULT_MAX_BYTES)
        {
            if (data.Length == 0) throw new InputError("empty input: pass the invoice XML or PDF bytes");
            if (data.Length > maxBytes) throw new InputError("file too large (max " + (maxBytes / (1024 * 1024)) + " MB)");
            int i = 0;
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\n' || data[i] == '\r' || data[i] == 0x0B || data[i] == 0x0C)) i++;
            if (!(data.Length - i >= 5 && data[i] == '%' && data[i + 1] == 'P' && data[i + 2] == 'D' && data[i + 3] == 'F' && data[i + 4] == '-'))
                return (data, "xml", null);
            IReadOnlyDictionary<string, byte[]> files;
            try { files = AttachmentReader.Read(data); }
            catch (DecompressionLimitError e) { throw new InputError(e.Message); } // public type only
            catch (InputError e) { throw e.Message.StartsWith("could not read PDF", StringComparison.Ordinal) ? e : new InputError("could not read PDF: " + e.Message); }
            catch (Exception e) when (!(e is OutOfMemoryException)) { throw new InputError("could not read PDF: " + e.Message); }
            if (files.Count == 0) throw new InputError("PDF has no embedded files: this is not a ZUGFeRD/Factur-X/XRechnung hybrid invoice");
            string? pick = files.Keys.FirstOrDefault(n => PDF_XML_NAMES.Contains(n.ToLowerInvariant()))
                ?? files.Keys.FirstOrDefault(n => n.ToLowerInvariant().EndsWith(".xml", StringComparison.Ordinal));
            if (pick == null)
            {
                var names = files.Keys.OrderBy(n => n, StringComparer.Ordinal).Select(n => "'" + n + "'");
                throw new InputError("PDF has embedded files but none is XML: [" + string.Join(", ", names) + "]");
            }
            if (files[pick].Length > maxBytes) throw new InputError("the embedded XML is too large (max " + (maxBytes / (1024 * 1024)) + " MB)");
            return (files[pick], "pdf", pick);
        }

        public static XmlDocument Parse(byte[] xml)
        {
            var head = Bytes.Latin1(xml.Length > 4096 ? xml.Take(4096).ToArray() : xml);
            if (head.Substring(0, Math.Min(2048, head.Length)).Contains("<!DOCTYPE") || head.Contains("<!ENTITY"))
                throw new InputError("DOCTYPE/ENTITY declarations are not allowed in e-invoices");
            var dom = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            try
            {
                using var ms = new System.IO.MemoryStream(xml);
                using var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false });
                dom.Load(r);
            }
            catch (XmlException e)
            {
                throw new InputError("not well-formed XML: " + e.Message);
            }
            if (dom.DocumentElement == null) throw new InputError("not well-formed XML: no root element");
            return dom;
        }

        public static XmlNamespaceManager Ns(XmlDocument dom)
        {
            var nsm = new XmlNamespaceManager(dom.NameTable);
            foreach (var p in new[] { "cbc", "cac", "rsm", "ram", "udt", "qdt" }) nsm.AddNamespace(p, Codes.NS[p]);
            nsm.AddNamespace("ubl", dom.DocumentElement?.NamespaceURI ?? Codes.NS["ubl_inv"]);
            return nsm;
        }

        private static Document Detect(XmlDocument dom)
        {
            var root = dom.DocumentElement!;
            var ns = root.NamespaceURI ?? "";
            var local = root.LocalName;
            string syntax, doctype;
            if (ns == Codes.NS["ubl_inv"] && local == "Invoice") (syntax, doctype) = ("ubl", "ubl_inv");
            else if (ns == Codes.NS["ubl_cn"] && local == "CreditNote") (syntax, doctype) = ("ubl", "ubl_cn");
            else if (ns == Codes.NS["cii"] && local == "CrossIndustryInvoice") (syntax, doctype) = ("cii", "cii");
            else throw new InputError($"unsupported root element {{{ns}}}{local}: expected UBL Invoice/CreditNote or UN/CEFACT CII");
            var nsm = Ns(dom);
            var q = syntax == "ubl" ? "cbc:CustomizationID" : "rsm:ExchangedDocumentContext/ram:GuidelineSpecifiedDocumentContextParameter/ram:ID";
            var n = root.SelectSingleNode(q, nsm);
            var cid = PyStrip(n?.InnerText ?? "");
            var c = cid.ToLowerInvariant();
            string profile;
            if (cid == Codes.XRECHNUNG_EXT_ID) profile = "XRechnung Extension";
            else if (c.Contains("xrechnung")) profile = "XRechnung";
            else if (c.Contains("peppol.eu:2017:poacc:billing")) profile = "Peppol BIS Billing 3.0";
            else if (c.Contains("factur-x.eu") || c.Contains("zugferd")) profile = "Factur-X/ZUGFeRD " + (c.Contains(":") ? c.Split(':').Last().ToUpperInvariant() : "");
            else if (c.StartsWith("urn:cen.eu:en16931:2017", StringComparison.Ordinal)) profile = !c.Contains("#") ? "EN 16931" : "EN 16931 CIUS/extension";
            else profile = "unknown";
            var notes = new List<string>();
            if (c.Contains("minimum") || c.Contains("basicwl"))
                notes.Add("Factur-X MINIMUM and BASIC WL are not EN 16931 compliant invoices; in DE/FR B2B they are not valid e-invoices on their own.");
            if (profile == "XRechnung Extension")
                notes.Add("XRechnung 3.0 Extension: checked with the EN 16931 and XRechnung rules using the severity levels of KoSIT's "
                    + "official Extension scenario (e.g. DIGA scheme IDs such as XR01/XR03 under BR-CL-10/11/21/25/26 are allowed and "
                    + "reported as warnings). The receiver must accept the Extension.");
            if (c.Contains("extended"))
                notes.Add("Factur-X EXTENDED goes beyond EN 16931: the EN 16931 CII syntax restrictions (CII-SR-*, CII-DT-*, e.g. "
                    + "'... should not be present') are reported as warnings, not errors, because EXTENDED allows those elements. "
                    + "EN 16931 business rules (BR-*) still apply as errors. The Factur-X EXTENDED schematron itself is not run.");
            return new Document { Dom = dom, Syntax = syntax, DocumentType = doctype, CustomizationId = cid, Profile = profile, Notes = notes };
        }

        private static bool IsPySpace(char ch) => char.IsWhiteSpace(ch) || (ch >= '\x1C' && ch <= '\x1F');

        /// <summary>Python str.strip() (Unicode whitespace)</summary>
        public static string PyStrip(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && IsPySpace(s[a])) a++;
            while (b > a && IsPySpace(s[b - 1])) b--;
            return s.Substring(a, b - a);
        }
    }
}
