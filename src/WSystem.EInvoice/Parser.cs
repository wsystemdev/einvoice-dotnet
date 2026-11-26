using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using WSystem.EInvoice.Support;
using WSystem.EInvoice.Validation;

namespace WSystem.EInvoice
{
    /// <summary>
    /// Reads an incoming EU e-invoice (UBL Invoice/CreditNote, UN/CEFACT CII, or the XML inside a ZUGFeRD / Factur-X /
    /// XRechnung PDF) into one flat structure keyed like the W System API's POST /einvoice/parse. It reads; it does not
    /// judge validity: use <see cref="Validator"/> for the rules. Values are strings as written in the XML.
    /// </summary>
    public static class Parser
    {
        public const string NOTE = "Values are read as written in the XML (strings, no rounding or recomputation). Reading does not check validity: "
            + "use EInvoice.Validate (or the W System API) for the EN 16931 / XRechnung rules. Not tax advice.";

        /// <returns>nested Dictionary&lt;string, object?&gt; / List&lt;object?&gt; / string / bool / long values</returns>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="InputError">The input is empty, too large, not well-formed XML, not a UBL / CII invoice, or a PDF
        /// without a readable embedded invoice XML (including PDFs whose decompressed data exceeds 75 MB).</exception>
        public static Dictionary<string, object?> Parse(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var d = Document.Load(data);
            var p = new Impl(Document.Ns(d.Dom));
            var root = d.Dom.DocumentElement!;
            var inv = d.Syntax == "cii" ? p.Cii(root) : p.Ubl(root, d.DocumentType == "ubl_cn");
            var container = new Dictionary<string, object?> { ["container"] = d.Container };
            if (d.EmbeddedFile != null) container["embedded_file"] = d.EmbeddedFile;
            var o = new Dictionary<string, object?>
            {
                ["syntax"] = d.Syntax,
                ["document_type"] = d.DocumentType == "ubl_cn" ? "credit_note" : "invoice",
                ["profile"] = d.Profile,
                ["customization_id"] = d.CustomizationId,
                ["container"] = container,
                ["invoice"] = Clean(inv),
                ["line_count"] = (long)((List<object?>)inv["lines"]!).Count,
                ["note"] = NOTE,
            };
            if (d.Syntax == "cii" && inv["type_code"] is string tc && (tc == "381" || tc == "396" || tc == "532")) o["document_type"] = "credit_note";
            return o;
        }

        /// <summary>Parse result as JSON (same shape as POST /einvoice/parse).</summary>
        public static string ParseToJson(byte[] data) => Json.Encode(Parse(data));

        private static object? Clean(object? d)
        {
            if (d is Dictionary<string, object?> dict)
            {
                var o = new Dictionary<string, object?>();
                foreach (var kv in dict)
                {
                    var v = Clean(kv.Value);
                    if (Empty(v)) continue;
                    o[kv.Key] = v;
                }
                return o;
            }
            if (d is List<object?> list)
            {
                var o = new List<object?>();
                foreach (var x in list) { var v = Clean(x); if (!Empty(v)) o.Add(v); }
                return o;
            }
            return d;
        }

        private static bool Empty(object? v) => v == null || (v is string s && s == "") || (v is Dictionary<string, object?> d && d.Count == 0) || (v is List<object?> l && l.Count == 0);

        private sealed class Impl
        {
            private readonly XmlNamespaceManager _ns;
            public Impl(XmlNamespaceManager ns) { _ns = ns; }

            private XmlElement? Find(XmlElement? el, string path) => el?.SelectSingleNode(path, _ns) as XmlElement;
            private List<XmlElement> FindAll(XmlElement? el, string path)
            {
                var o = new List<XmlElement>();
                if (el == null) return o;
                foreach (XmlNode n in el.SelectNodes(path, _ns)!) if (n is XmlElement e) o.Add(e);
                return o;
            }

            /// <summary>ElementTree .text: the character data before the first child node that isn't text</summary>
            private static string? Text(XmlElement e)
            {
                string? s = null;
                foreach (XmlNode c in e.ChildNodes)
                {
                    if (c is XmlCharacterData cd && !(c is XmlComment)) s = (s ?? "") + cd.Data;
                    else break;
                }
                return s;
            }

            private string? T(XmlElement? el, string path)
            {
                var x = Find(el, path);
                if (x == null) return null;
                var v = Document.PyStrip(Text(x) ?? "");
                return v == "" ? null : v;
            }

            private string? Attr(XmlElement? el, string path, string attr)
            {
                var x = Find(el, path);
                return x != null && x.HasAttribute(attr) ? x.GetAttribute(attr) : null;
            }

            private static Dictionary<string, object?> D(params (string K, object? V)[] kv)
            {
                var d = new Dictionary<string, object?>();
                foreach (var (k, v) in kv) d[k] = v;
                return d;
            }

            // ------------------------------------------------------------------ UBL
            private Dictionary<string, object?>? UblParty(XmlElement? p)
            {
                if (p == null) return null;
                string? vat = null; string? taxReg = null; bool haveTaxReg = false;
                foreach (var pts in FindAll(p, "cac:PartyTaxScheme"))
                {
                    if ((T(pts, "cac:TaxScheme/cbc:ID") ?? "").ToUpperInvariant() == "VAT") vat = T(pts, "cbc:CompanyID");
                    else if (!haveTaxReg) { taxReg = T(pts, "cbc:CompanyID"); haveTaxReg = true; }
                }
                var a = Find(p, "cac:PostalAddress");
                return D(
                    ("name", T(p, "cac:PartyLegalEntity/cbc:RegistrationName") ?? T(p, "cac:PartyName/cbc:Name")),
                    ("trading_name", T(p, "cac:PartyName/cbc:Name")),
                    ("vat_id", vat), ("tax_registration_id", taxReg),
                    ("legal_registration_id", T(p, "cac:PartyLegalEntity/cbc:CompanyID")),
                    ("legal_registration_scheme", Attr(p, "cac:PartyLegalEntity/cbc:CompanyID", "schemeID")),
                    ("electronic_address", T(p, "cbc:EndpointID")),
                    ("electronic_address_scheme", Attr(p, "cbc:EndpointID", "schemeID")),
                    ("address", D(("street", T(a, "cbc:StreetName")), ("additional", T(a, "cbc:AdditionalStreetName")),
                        ("city", T(a, "cbc:CityName")), ("postal_code", T(a, "cbc:PostalZone")),
                        ("subdivision", T(a, "cbc:CountrySubentity")), ("country", T(a, "cac:Country/cbc:IdentificationCode")))),
                    ("contact", D(("name", T(p, "cac:Contact/cbc:Name")), ("phone", T(p, "cac:Contact/cbc:Telephone")),
                        ("email", T(p, "cac:Contact/cbc:ElectronicMail")))));
            }

            public Dictionary<string, object?> Ubl(XmlElement root, bool cn)
            {
                var lt = cn ? "cac:CreditNoteLine" : "cac:InvoiceLine";
                var qt = cn ? "cbc:CreditedQuantity" : "cbc:InvoicedQuantity";
                var tot = Find(root, "cac:LegalMonetaryTotal");
                var pay = FindAll(root, "cac:PaymentMeans").Select(pm => (object?)D(
                    ("means_code", T(pm, "cbc:PaymentMeansCode")), ("remittance_information", T(pm, "cbc:PaymentID")),
                    ("iban", T(pm, "cac:PayeeFinancialAccount/cbc:ID")), ("account_name", T(pm, "cac:PayeeFinancialAccount/cbc:Name")),
                    ("bic", T(pm, "cac:PayeeFinancialAccount/cac:FinancialInstitutionBranch/cbc:ID")),
                    ("mandate_reference", T(pm, "cac:PaymentMandate/cbc:ID")),
                    ("debited_account", T(pm, "cac:PaymentMandate/cac:PayerFinancialAccount/cbc:ID")))).ToList();
                var vat = FindAll(root, "cac:TaxTotal/cac:TaxSubtotal").Select(st => (object?)D(
                    ("category", T(st, "cac:TaxCategory/cbc:ID")), ("rate", T(st, "cac:TaxCategory/cbc:Percent")),
                    ("taxable_amount", T(st, "cbc:TaxableAmount")), ("tax_amount", T(st, "cbc:TaxAmount")),
                    ("exemption_reason", T(st, "cac:TaxCategory/cbc:TaxExemptionReason")),
                    ("exemption_reason_code", T(st, "cac:TaxCategory/cbc:TaxExemptionReasonCode")))).ToList();
                string? taxTotal = null;
                var cur = T(root, "cbc:DocumentCurrencyCode");
                foreach (var tt in FindAll(root, "cac:TaxTotal"))
                {
                    var c = Attr(tt, "cbc:TaxAmount", "currencyID");
                    if (c == null || c == cur) taxTotal ??= T(tt, "cbc:TaxAmount");
                }
                var lines = FindAll(root, lt).Select(ln => (object?)D(
                    ("id", T(ln, "cbc:ID")), ("name", T(ln, "cac:Item/cbc:Name")), ("description", T(ln, "cac:Item/cbc:Description")),
                    ("quantity", T(ln, qt)), ("unit", Attr(ln, qt, "unitCode")),
                    ("unit_price", T(ln, "cac:Price/cbc:PriceAmount")), ("net_amount", T(ln, "cbc:LineExtensionAmount")),
                    ("vat_category", T(ln, "cac:Item/cac:ClassifiedTaxCategory/cbc:ID")),
                    ("vat_rate", T(ln, "cac:Item/cac:ClassifiedTaxCategory/cbc:Percent")),
                    ("seller_item_id", T(ln, "cac:Item/cac:SellersItemIdentification/cbc:ID")),
                    ("standard_item_id", T(ln, "cac:Item/cac:StandardItemIdentification/cbc:ID")),
                    ("note", T(ln, "cbc:Note")))).ToList();
                var notes = new List<object?>();
                foreach (var n in FindAll(root, "cbc:Note"))
                {
                    var s = Text(n);
                    if (s != null && Document.PyStrip(s) != "") notes.Add(Document.PyStrip(s));
                }
                var att = FindAll(root, "cac:AdditionalDocumentReference").Select(d => (object?)D(
                    ("id", T(d, "cbc:ID")), ("description", T(d, "cbc:DocumentDescription")),
                    ("filename", Attr(d, "cac:Attachment/cbc:EmbeddedDocumentBinaryObject", "filename")),
                    ("mime_code", Attr(d, "cac:Attachment/cbc:EmbeddedDocumentBinaryObject", "mimeCode")),
                    ("embedded", Find(d, "cac:Attachment/cbc:EmbeddedDocumentBinaryObject") != null))).ToList();
                return D(
                    ("invoice_number", T(root, "cbc:ID")), ("issue_date", T(root, "cbc:IssueDate")),
                    ("type_code", T(root, cn ? "cbc:CreditNoteTypeCode" : "cbc:InvoiceTypeCode")),
                    ("currency", cur), ("due_date", T(root, "cbc:DueDate") ?? T(root, "cac:PaymentMeans/cbc:PaymentDueDate")),
                    ("tax_point_date", T(root, "cbc:TaxPointDate")), ("buyer_reference", T(root, "cbc:BuyerReference")),
                    ("order_reference", T(root, "cac:OrderReference/cbc:ID")), ("contract_reference", T(root, "cac:ContractDocumentReference/cbc:ID")),
                    ("preceding_invoices", FindAll(root, "cac:BillingReference").Select(b => (object?)T(b, "cac:InvoiceDocumentReference/cbc:ID")).ToList()),
                    ("notes", notes),
                    ("payment_terms", T(root, "cac:PaymentTerms/cbc:Note")),
                    ("seller", UblParty(Find(root, "cac:AccountingSupplierParty/cac:Party"))),
                    ("buyer", UblParty(Find(root, "cac:AccountingCustomerParty/cac:Party"))),
                    ("delivery", D(("date", T(root, "cac:Delivery/cbc:ActualDeliveryDate")), ("party_name", T(root, "cac:Delivery/cac:DeliveryParty/cac:PartyName/cbc:Name")),
                        ("country", T(root, "cac:Delivery/cac:DeliveryLocation/cac:Address/cac:Country/cbc:IdentificationCode")))),
                    ("totals", D(("line_total", T(tot, "cbc:LineExtensionAmount")), ("allowance_total", T(tot, "cbc:AllowanceTotalAmount")),
                        ("charge_total", T(tot, "cbc:ChargeTotalAmount")), ("total_without_vat", T(tot, "cbc:TaxExclusiveAmount")),
                        ("vat_total", taxTotal), ("total_with_vat", T(tot, "cbc:TaxInclusiveAmount")),
                        ("prepaid", T(tot, "cbc:PrepaidAmount")), ("rounding", T(tot, "cbc:PayableRoundingAmount")),
                        ("amount_due", T(tot, "cbc:PayableAmount")))),
                    ("vat_breakdown", vat), ("payment", pay), ("lines", lines), ("attachments", att));
            }

            // ------------------------------------------------------------------ CII
            private Dictionary<string, object?>? CiiParty(XmlElement? p)
            {
                if (p == null) return null;
                string? vat = null, taxReg = null;
                foreach (var r in FindAll(p, "ram:SpecifiedTaxRegistration"))
                {
                    var sid = Attr(r, "ram:ID", "schemeID");
                    if (sid == "VA") vat = T(r, "ram:ID");
                    else if (sid == "FC") taxReg = T(r, "ram:ID");
                }
                var a = Find(p, "ram:PostalTradeAddress");
                var ct = Find(p, "ram:DefinedTradeContact");
                return D(
                    ("name", T(p, "ram:Name")), ("trading_name", T(p, "ram:SpecifiedLegalOrganization/ram:TradingBusinessName")),
                    ("vat_id", vat), ("tax_registration_id", taxReg),
                    ("legal_registration_id", T(p, "ram:SpecifiedLegalOrganization/ram:ID")),
                    ("legal_registration_scheme", Attr(p, "ram:SpecifiedLegalOrganization/ram:ID", "schemeID")),
                    ("electronic_address", T(p, "ram:URIUniversalCommunication/ram:URIID")),
                    ("electronic_address_scheme", Attr(p, "ram:URIUniversalCommunication/ram:URIID", "schemeID")),
                    ("address", D(("street", T(a, "ram:LineOne")), ("additional", T(a, "ram:LineTwo")), ("city", T(a, "ram:CityName")),
                        ("postal_code", T(a, "ram:PostcodeCode")), ("subdivision", T(a, "ram:CountrySubDivisionName")),
                        ("country", T(a, "ram:CountryID")))),
                    ("contact", D(("name", T(ct, "ram:PersonName") ?? T(ct, "ram:DepartmentName")),
                        ("phone", T(ct, "ram:TelephoneUniversalCommunication/ram:CompleteNumber")),
                        ("email", T(ct, "ram:EmailURIUniversalCommunication/ram:URIID")))));
            }

            private string? CiiDateAny(XmlElement? el, string path)
            {
                var x = Find(el, path);
                if (x == null) return null;
                string? s = null;
                foreach (XmlNode c in x.SelectNodes(".//*", _ns)!)
                {
                    if (!(c is XmlElement ce)) continue;
                    var tx = Text(ce);
                    if (tx != null && Document.PyStrip(tx) != "") { s = Document.PyStrip(tx); break; }
                }
                if (s != null && s.Length == 8 && s.All(ch => ch >= '0' && ch <= '9'))
                    return s.Substring(0, 4) + "-" + s.Substring(4, 2) + "-" + s.Substring(6);
                return s;
            }

            public Dictionary<string, object?> Cii(XmlElement root)
            {
                var tx = Find(root, "rsm:SupplyChainTradeTransaction");
                var ag = Find(tx, "ram:ApplicableHeaderTradeAgreement");
                var de = Find(tx, "ram:ApplicableHeaderTradeDelivery");
                var st = Find(tx, "ram:ApplicableHeaderTradeSettlement");
                var sm = Find(st, "ram:SpecifiedTradeSettlementHeaderMonetarySummation");
                var cur = T(st, "ram:InvoiceCurrencyCode");
                string? vatTotal = null;
                foreach (var e in FindAll(sm, "ram:TaxTotalAmount"))
                {
                    var c = e.HasAttribute("currencyID") ? e.GetAttribute("currencyID") : null;
                    var tx2 = Text(e);
                    if ((c == null || c == cur) && tx2 != null && tx2 != "")
                        vatTotal = string.IsNullOrEmpty(vatTotal) ? Document.PyStrip(tx2) : vatTotal;
                }
                var pay = FindAll(st, "ram:SpecifiedTradeSettlementPaymentMeans").Select(pm => (object?)D(
                    ("means_code", T(pm, "ram:TypeCode")), ("remittance_information", T(st, "ram:PaymentReference")),
                    ("iban", T(pm, "ram:PayeePartyCreditorFinancialAccount/ram:IBANID") ?? T(pm, "ram:PayeePartyCreditorFinancialAccount/ram:ProprietaryID")),
                    ("account_name", T(pm, "ram:PayeePartyCreditorFinancialAccount/ram:AccountName")),
                    ("bic", T(pm, "ram:PayeeSpecifiedCreditorFinancialInstitution/ram:BICID")),
                    ("mandate_reference", T(st, "ram:SpecifiedTradePaymentTerms/ram:DirectDebitMandateID")),
                    ("debited_account", T(pm, "ram:PayerPartyDebtorFinancialAccount/ram:IBANID")))).ToList();
                var vat = FindAll(st, "ram:ApplicableTradeTax").Select(t => (object?)D(
                    ("category", T(t, "ram:CategoryCode")), ("rate", T(t, "ram:RateApplicablePercent")),
                    ("taxable_amount", T(t, "ram:BasisAmount")), ("tax_amount", T(t, "ram:CalculatedAmount")),
                    ("exemption_reason", T(t, "ram:ExemptionReason")), ("exemption_reason_code", T(t, "ram:ExemptionReasonCode")))).ToList();
                const string q = "ram:SpecifiedLineTradeDelivery/ram:BilledQuantity";
                var lines = FindAll(tx, "ram:IncludedSupplyChainTradeLineItem").Select(ln => (object?)D(
                    ("id", T(ln, "ram:AssociatedDocumentLineDocument/ram:LineID")), ("name", T(ln, "ram:SpecifiedTradeProduct/ram:Name")),
                    ("description", T(ln, "ram:SpecifiedTradeProduct/ram:Description")), ("quantity", T(ln, q)), ("unit", Attr(ln, q, "unitCode")),
                    ("unit_price", T(ln, "ram:SpecifiedLineTradeAgreement/ram:NetPriceProductTradePrice/ram:ChargeAmount")),
                    ("net_amount", T(ln, "ram:SpecifiedLineTradeSettlement/ram:SpecifiedTradeSettlementLineMonetarySummation/ram:LineTotalAmount")),
                    ("vat_category", T(ln, "ram:SpecifiedLineTradeSettlement/ram:ApplicableTradeTax/ram:CategoryCode")),
                    ("vat_rate", T(ln, "ram:SpecifiedLineTradeSettlement/ram:ApplicableTradeTax/ram:RateApplicablePercent")),
                    ("seller_item_id", T(ln, "ram:SpecifiedTradeProduct/ram:SellerAssignedID")),
                    ("standard_item_id", T(ln, "ram:SpecifiedTradeProduct/ram:GlobalID")),
                    ("note", T(ln, "ram:AssociatedDocumentLineDocument/ram:IncludedNote/ram:Content")))).ToList();
                var doc = Find(root, "rsm:ExchangedDocument");
                var att = FindAll(ag, "ram:AdditionalReferencedDocument").Select(d => (object?)D(
                    ("id", T(d, "ram:IssuerAssignedID")), ("description", T(d, "ram:Name")),
                    ("filename", Attr(d, "ram:AttachmentBinaryObject", "filename")), ("mime_code", Attr(d, "ram:AttachmentBinaryObject", "mimeCode")),
                    ("embedded", Find(d, "ram:AttachmentBinaryObject") != null))).ToList();
                return D(
                    ("invoice_number", T(doc, "ram:ID")), ("issue_date", CiiDateAny(doc, "ram:IssueDateTime")),
                    ("type_code", T(doc, "ram:TypeCode")), ("currency", cur),
                    ("due_date", CiiDateAny(st, "ram:SpecifiedTradePaymentTerms/ram:DueDateDateTime")),
                    ("tax_point_date", CiiDateAny(st, "ram:ApplicableTradeTax/ram:TaxPointDate")),
                    ("buyer_reference", T(ag, "ram:BuyerReference")), ("order_reference", T(ag, "ram:BuyerOrderReferencedDocument/ram:IssuerAssignedID")),
                    ("contract_reference", T(ag, "ram:ContractReferencedDocument/ram:IssuerAssignedID")),
                    ("preceding_invoices", FindAll(st, "ram:InvoiceReferencedDocument").Select(b => (object?)T(b, "ram:IssuerAssignedID")).ToList()),
                    ("notes", FindAll(doc, "ram:IncludedNote").Select(n => (object?)T(n, "ram:Content")).ToList()),
                    ("payment_terms", T(st, "ram:SpecifiedTradePaymentTerms/ram:Description")),
                    ("seller", CiiParty(Find(ag, "ram:SellerTradeParty"))),
                    ("buyer", CiiParty(Find(ag, "ram:BuyerTradeParty"))),
                    ("delivery", D(("date", CiiDateAny(de, "ram:ActualDeliverySupplyChainEvent/ram:OccurrenceDateTime")),
                        ("party_name", T(de, "ram:ShipToTradeParty/ram:Name")), ("country", T(de, "ram:ShipToTradeParty/ram:PostalTradeAddress/ram:CountryID")))),
                    ("totals", D(("line_total", T(sm, "ram:LineTotalAmount")), ("allowance_total", T(sm, "ram:AllowanceTotalAmount")),
                        ("charge_total", T(sm, "ram:ChargeTotalAmount")), ("total_without_vat", T(sm, "ram:TaxBasisTotalAmount")),
                        ("vat_total", vatTotal), ("total_with_vat", T(sm, "ram:GrandTotalAmount")), ("prepaid", T(sm, "ram:TotalPrepaidAmount")),
                        ("rounding", T(sm, "ram:RoundingAmount")), ("amount_due", T(sm, "ram:DuePayableAmount")))),
                    ("vat_breakdown", vat), ("payment", pay), ("lines", lines), ("attachments", att));
            }
        }
    }
}
