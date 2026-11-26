using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Generator
{
    /// <summary>UBL 2.1 Invoice / CreditNote builder (formats en16931, xrechnung, peppol-bis3).</summary>
    internal static class UblGenerator
    {
        public static readonly string[] FORMATS = { "en16931", "xrechnung", "peppol-bis3" };

        private static string Taxcat(string cat, PyDecimal rate, string tag = "cac:ClassifiedTaxCategory")
        {
            var s = $"<{tag}><cbc:ID>" + Py.X(cat) + "</cbc:ID>";
            if (cat != "O") s += "<cbc:Percent>" + rate.Num() + "</cbc:Percent>";
            return s + $"<cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></{tag}>";
        }

        private static string AcXml(Ac a, string cur, bool doc)
        {
            var s = "<cac:AllowanceCharge><cbc:ChargeIndicator>" + (a.Charge ? "true" : "false") + "</cbc:ChargeIndicator>";
            if (Py.T(a.ReasonCode)) s += "<cbc:AllowanceChargeReasonCode>" + Py.X(a.ReasonCode) + "</cbc:AllowanceChargeReasonCode>";
            if (Py.T(a.Reason)) s += "<cbc:AllowanceChargeReason>" + Py.X(a.Reason) + "</cbc:AllowanceChargeReason>";
            if (a.Percent != null) s += "<cbc:MultiplierFactorNumeric>" + a.Percent.Num() + "</cbc:MultiplierFactorNumeric>";
            s += $"<cbc:Amount currencyID=\"{cur}\">" + a.Amount.A() + "</cbc:Amount>";
            if (a.Base != null) s += $"<cbc:BaseAmount currencyID=\"{cur}\">" + a.Base.A() + "</cbc:BaseAmount>";
            if (doc) s += Taxcat(a.Cat, a.Rate, "cac:TaxCategory");
            return s + "</cac:AllowanceCharge>";
        }

        private static readonly Regex NoVat = new Regex(@"^NO[0-9]{9}(MVA)?\z", RegexOptions.CultureInvariant);

        private static string Party(object? p0, string role, string fmt)
        {
            var p = p0 is Dictionary<string, object?> d ? new Dictionary<string, object?>(d) : new Dictionary<string, object?>();
            foreach (var f in new[] { "name", "country" })
                if (!Py.T(Py.G(p, f))) throw new InputError($"{role}.{f} is required");
            var cc = Py.Upper(p["country"]);
            var (eid, esch) = Codes.Endpoint(p, fmt, role);
            var parts = new StringBuilder();
            if (Py.T(eid)) parts.Append("<cbc:EndpointID schemeID=\"" + Py.X(esch) + "\">" + Py.X(eid) + "</cbc:EndpointID>");
            if (Py.T(Py.G(p, "trading_name"))) parts.Append("<cac:PartyName><cbc:Name>" + Py.X(p["trading_name"]) + "</cbc:Name></cac:PartyName>");
            var addr = "<cac:PostalAddress>";
            if (Py.T(Py.G(p, "street"))) addr += "<cbc:StreetName>" + Py.X(p["street"]) + "</cbc:StreetName>";
            if (Py.T(Py.G(p, "city"))) addr += "<cbc:CityName>" + Py.X(p["city"]) + "</cbc:CityName>";
            if (Py.T(Py.G(p, "postal_code"))) addr += "<cbc:PostalZone>" + Py.X(p["postal_code"]) + "</cbc:PostalZone>";
            addr += "<cac:Country><cbc:IdentificationCode>" + Py.X(cc) + "</cbc:IdentificationCode></cac:Country></cac:PostalAddress>";
            parts.Append(addr);
            string? vat = Py.T(Py.G(p, "vat_id")) ? Py.Upper(Py.Str(p["vat_id"]).Replace(" ", "")) : null;
            if (vat != null) parts.Append("<cac:PartyTaxScheme><cbc:CompanyID>" + Py.X(vat) + "</cbc:CompanyID><cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:PartyTaxScheme>");
            if (fmt == "peppol-bis3" && role == "seller" && cc == "NO" && !Py.T(Py.G(p, "tax_number"))
                && new[] { "AS", "ASA", "NUF" }.Contains(Py.Upper(Py.T(Py.G(p, "legal_form")) ? p["legal_form"] : "")))
                parts.Append("<cac:PartyTaxScheme><cbc:CompanyID>Foretaksregisteret</cbc:CompanyID><cac:TaxScheme><cbc:ID>TAX</cbc:ID></cac:TaxScheme></cac:PartyTaxScheme>");
            if (Py.T(Py.G(p, "tax_number"))) parts.Append("<cac:PartyTaxScheme><cbc:CompanyID>" + Py.X(p["tax_number"]) + "</cbc:CompanyID><cac:TaxScheme><cbc:ID>FC</cbc:ID></cac:TaxScheme></cac:PartyTaxScheme>");
            var le = "<cac:PartyLegalEntity><cbc:RegistrationName>" + Py.X(p["name"]) + "</cbc:RegistrationName>";
            if (!Py.T(Py.G(p, "company_id")) && fmt == "peppol-bis3" && cc == "NO" && vat != null && NoVat.IsMatch(vat))
            {
                p["company_id"] = vat.Substring(2, 9);
                p["company_id_scheme"] = "0192";
            }
            if (Py.T(Py.G(p, "company_id")))
            {
                var sch = Py.T(Py.G(p, "company_id_scheme")) ? " schemeID=\"" + Py.X(p["company_id_scheme"]) + "\"" : "";
                le += $"<cbc:CompanyID{sch}>" + Py.X(p["company_id"]) + "</cbc:CompanyID>";
            }
            if (Py.T(Py.G(p, "legal_form")) && role == "seller") le += "<cbc:CompanyLegalForm>" + Py.X(p["legal_form"]) + "</cbc:CompanyLegalForm>";
            parts.Append(le + "</cac:PartyLegalEntity>");
            if (Py.T(Py.G(p, "contact_name")) || Py.T(Py.G(p, "phone")) || Py.T(Py.G(p, "email")))
            {
                var c = "<cac:Contact>";
                if (Py.T(Py.G(p, "contact_name"))) c += "<cbc:Name>" + Py.X(p["contact_name"]) + "</cbc:Name>";
                if (Py.T(Py.G(p, "phone"))) c += "<cbc:Telephone>" + Py.X(p["phone"]) + "</cbc:Telephone>";
                if (Py.T(Py.G(p, "email"))) c += "<cbc:ElectronicMail>" + Py.X(p["email"]) + "</cbc:ElectronicMail>";
                parts.Append(c + "</cac:Contact>");
            }
            return "<cac:Party>" + parts + "</cac:Party>";
        }

        public static (string Xml, Dictionary<string, string> Totals, Money Model) Build(object? j)
        {
            var fmt = Py.Lower(Py.T(Py.G(j, "format")) ? Py.G(j, "format") : "en16931");
            if (!FORMATS.Contains(fmt))
                throw new InputError("format must be en16931, xrechnung, peppol-bis3 (UBL), cii, xrechnung-cii (CII) or zugferd / factur-x / zugferd-xrechnung (PDF)");
            var m = MoneyModel.Compute(j, false);
            var cur = m.Cur;
            var isCn = m.IsCn;
            var linesXml = new List<string>();
            var qtag = isCn ? "cbc:CreditedQuantity" : "cbc:InvoicedQuantity";
            var ltag = isCn ? "cac:CreditNoteLine" : "cac:InvoiceLine";
            foreach (var ln in m.Lines)
            {
                var lineAc = string.Concat(ln.Acs.Select(a => AcXml(a, cur, false)));
                var item = "<cac:Item>";
                if (Py.T(ln.Desc)) item += "<cbc:Description>" + Py.X(ln.Desc) + "</cbc:Description>";
                item += "<cbc:Name>" + Py.X(ln.Name) + "</cbc:Name>";
                if (Py.T(ln.Sid)) item += "<cac:SellersItemIdentification><cbc:ID>" + Py.X(ln.Sid) + "</cbc:ID></cac:SellersItemIdentification>";
                item += Taxcat(ln.Cat, ln.Rate) + "</cac:Item>";
                linesXml.Add($"<{ltag}><cbc:ID>{ln.Id}</cbc:ID><{qtag} unitCode=\"" + Py.X(ln.Unit) + "\">" + ln.Q.Num() + $"</{qtag}>"
                    + $"<cbc:LineExtensionAmount currencyID=\"{cur}\">" + ln.Net.A() + $"</cbc:LineExtensionAmount>{lineAc}{item}"
                    + $"<cac:Price><cbc:PriceAmount currencyID=\"{cur}\">" + ln.Price.Num() + $"</cbc:PriceAmount></cac:Price></{ltag}>");
            }
            var subt = new StringBuilder();
            foreach (var g in m.Vat)
            {
                var s = $"<cac:TaxSubtotal><cbc:TaxableAmount currencyID=\"{cur}\">" + g.Base.A() + $"</cbc:TaxableAmount><cbc:TaxAmount currencyID=\"{cur}\">"
                    + g.Tax.A() + "</cbc:TaxAmount><cac:TaxCategory><cbc:ID>" + g.Cat + "</cbc:ID>";
                if (g.Cat != "O") s += "<cbc:Percent>" + g.Rate.Num() + "</cbc:Percent>";
                if (g.Ex)
                {
                    if (Py.T(g.ExCode)) s += "<cbc:TaxExemptionReasonCode>" + Py.X(g.ExCode) + "</cbc:TaxExemptionReasonCode>";
                    s += "<cbc:TaxExemptionReason>" + Py.X(g.ExText) + "</cbc:TaxExemptionReason>";
                }
                s += "<cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:TaxCategory></cac:TaxSubtotal>";
                subt.Append(s);
            }
            var cid = fmt == "en16931" ? Codes.EN16931_ID : fmt == "xrechnung" ? Codes.XRECHNUNG_ID : Codes.PEPPOL_ID;
            var (rootTag, rootNs) = isCn ? ("CreditNote", Codes.NS["ubl_cn"]) : ("Invoice", Codes.NS["ubl_inv"]);
            var x = new StringBuilder();
            x.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<{rootTag} xmlns=\"{rootNs}\" xmlns:cac=\"" + Codes.NS["cac"] + "\" xmlns:cbc=\"" + Codes.NS["cbc"] + "\">");
            x.Append($"<cbc:CustomizationID>{cid}</cbc:CustomizationID>");
            if (fmt == "peppol-bis3" || fmt == "xrechnung") x.Append("<cbc:ProfileID>" + Codes.PEPPOL_PROFILE + "</cbc:ProfileID>");
            x.Append("<cbc:ID>" + Py.X(Py.G(j, "invoice_number")) + "</cbc:ID><cbc:IssueDate>" + Py.X(Py.G(j, "issue_date")) + "</cbc:IssueDate>");
            if (Py.T(Py.G(j, "due_date")) && !isCn) x.Append("<cbc:DueDate>" + Py.X(Py.G(j, "due_date")) + "</cbc:DueDate>");
            var tt = isCn ? "CreditNoteTypeCode" : "InvoiceTypeCode";
            x.Append($"<cbc:{tt}>" + Py.X(Py.Has(j, "type_code") ? Py.G(j, "type_code") : (isCn ? "381" : "380")) + $"</cbc:{tt}>");
            if (Py.T(Py.G(j, "note"))) x.Append("<cbc:Note>" + Py.X(Py.G(j, "note")) + "</cbc:Note>");
            if (Py.T(Py.G(j, "tax_point_date")))
            {
                if (!Codes.IsDate(Py.G(j, "tax_point_date"))) throw new InputError("tax_point_date must be YYYY-MM-DD");
                x.Append("<cbc:TaxPointDate>" + Py.X(Py.G(j, "tax_point_date")) + "</cbc:TaxPointDate>");
            }
            x.Append($"<cbc:DocumentCurrencyCode>{cur}</cbc:DocumentCurrencyCode>");
            var bref = Py.V(j, "buyer_reference");
            if (bref == null && !(Py.T(Py.G(j, "order_reference")) || fmt == "en16931") && fmt == "peppol-bis3") bref = Py.G(j, "invoice_number");
            if (Py.T(bref)) x.Append("<cbc:BuyerReference>" + Py.X(bref) + "</cbc:BuyerReference>");
            if (Py.T(Py.G(j, "order_reference"))) x.Append("<cac:OrderReference><cbc:ID>" + Py.X(Py.G(j, "order_reference")) + "</cbc:ID></cac:OrderReference>");
            if (Py.T(Py.G(j, "preceding_invoice")))
                x.Append("<cac:BillingReference><cac:InvoiceDocumentReference><cbc:ID>" + Py.X(Py.G(j, "preceding_invoice")) + "</cbc:ID></cac:InvoiceDocumentReference></cac:BillingReference>");
            x.Append("<cac:AccountingSupplierParty>" + Party(Py.G(j, "seller"), "seller", fmt) + "</cac:AccountingSupplierParty>");
            x.Append("<cac:AccountingCustomerParty>" + Party(Py.G(j, "buyer"), "buyer", fmt) + "</cac:AccountingCustomerParty>");
            if (Py.T(Py.G(j, "delivery_date")) || Py.T(Py.G(j, "delivery_country")))
            {
                var dl = Py.T(Py.G(j, "delivery_date")) ? "<cbc:ActualDeliveryDate>" + Py.X(Py.G(j, "delivery_date")) + "</cbc:ActualDeliveryDate>" : "";
                if (Py.T(Py.G(j, "delivery_country")))
                    dl += "<cac:DeliveryLocation><cac:Address><cac:Country><cbc:IdentificationCode>" + Py.X(Py.Upper(Py.G(j, "delivery_country")))
                        + "</cbc:IdentificationCode></cac:Country></cac:Address></cac:DeliveryLocation>";
                x.Append($"<cac:Delivery>{dl}</cac:Delivery>");
            }
            var pay0 = Py.G(j, "payment");
            object? pay = Py.T(pay0) && (pay0 is Dictionary<string, object?> || pay0 is List<object?>) ? pay0 : new Dictionary<string, object?>();
            if (Py.T(Py.G(pay, "iban")) || Py.T(Py.G(pay, "means_code")))
            {
                var code = Py.Has(pay, "means_code") ? Py.G(pay, "means_code") : (Py.T(Py.G(pay, "iban")) ? "58" : "1");
                var pm = "<cac:PaymentMeans><cbc:PaymentMeansCode>" + Py.X(code) + "</cbc:PaymentMeansCode>";
                if (Py.T(Py.G(pay, "reference"))) pm += "<cbc:PaymentID>" + Py.X(Py.G(pay, "reference")) + "</cbc:PaymentID>";
                if (Py.T(Py.G(pay, "iban")))
                {
                    pm += "<cac:PayeeFinancialAccount><cbc:ID>" + Py.X(Py.Str(Py.G(pay, "iban")).Replace(" ", "")) + "</cbc:ID>";
                    if (Py.T(Py.G(pay, "account_name"))) pm += "<cbc:Name>" + Py.X(Py.G(pay, "account_name")) + "</cbc:Name>";
                    if (Py.T(Py.G(pay, "bic"))) pm += "<cac:FinancialInstitutionBranch><cbc:ID>" + Py.X(Py.G(pay, "bic")) + "</cbc:ID></cac:FinancialInstitutionBranch>";
                    pm += "</cac:PayeeFinancialAccount>";
                }
                x.Append(pm + "</cac:PaymentMeans>");
            }
            if (Py.T(Py.G(pay, "terms"))) x.Append("<cac:PaymentTerms><cbc:Note>" + Py.X(Py.G(pay, "terms")) + "</cbc:Note></cac:PaymentTerms>");
            foreach (var a in m.DocAcs) x.Append(AcXml(a, cur, true));
            x.Append($"<cac:TaxTotal><cbc:TaxAmount currencyID=\"{cur}\">" + m.VatTotal.A() + "</cbc:TaxAmount>" + subt + "</cac:TaxTotal>");
            var lmt = $"<cac:LegalMonetaryTotal><cbc:LineExtensionAmount currencyID=\"{cur}\">" + m.LineTotal.A() + "</cbc:LineExtensionAmount>"
                + $"<cbc:TaxExclusiveAmount currencyID=\"{cur}\">" + m.Basis.A() + "</cbc:TaxExclusiveAmount>"
                + $"<cbc:TaxInclusiveAmount currencyID=\"{cur}\">" + m.Incl.A() + "</cbc:TaxInclusiveAmount>";
            if (m.DocAcs.Count > 0)
                lmt += $"<cbc:AllowanceTotalAmount currencyID=\"{cur}\">" + m.AllowTotal.A() + "</cbc:AllowanceTotalAmount>"
                    + $"<cbc:ChargeTotalAmount currencyID=\"{cur}\">" + m.ChargeTotal.A() + "</cbc:ChargeTotalAmount>";
            if (!m.Prepaid.IsZero) lmt += $"<cbc:PrepaidAmount currencyID=\"{cur}\">" + m.Prepaid.A() + "</cbc:PrepaidAmount>";
            x.Append(lmt + $"<cbc:PayableAmount currencyID=\"{cur}\">" + m.Payable.A() + "</cbc:PayableAmount></cac:LegalMonetaryTotal>");
            foreach (var l in linesXml) x.Append(l);
            x.Append($"</{rootTag}>");
            return (x.ToString(), MoneyModel.Totals(m), m);
        }
    }
}
