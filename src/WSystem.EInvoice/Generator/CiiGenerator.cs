using System.Collections.Generic;
using System.Linq;
using System.Text;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Support;

namespace WSystem.EInvoice.Generator
{
    /// <summary>UN/CEFACT CII D16B builder (cii, xrechnung-cii and the XML of zugferd / factur-x / zugferd-xrechnung).</summary>
    internal static class CiiGenerator
    {
        public static readonly (string Prefix, string Ns)[] CII_NS =
        {
            ("rsm", "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"),
            ("ram", "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100"),
            ("qdt", "urn:un:unece:uncefact:data:standard:QualifiedDataType:100"),
            ("udt", "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"),
        };

        public sealed class FormatInfo
        {
            public string Gid = ""; public bool IsPdf; public string? FileName; public string? Level;
        }

        public static readonly Dictionary<string, FormatInfo> FORMATS = new Dictionary<string, FormatInfo>
        {
            ["cii"] = new FormatInfo { Gid = Codes.EN16931_ID },
            ["xrechnung-cii"] = new FormatInfo { Gid = Codes.XRECHNUNG_ID },
            ["zugferd"] = new FormatInfo { Gid = Codes.EN16931_ID, IsPdf = true, FileName = "factur-x.xml", Level = "EN 16931" },
            ["factur-x"] = new FormatInfo { Gid = Codes.EN16931_ID, IsPdf = true, FileName = "factur-x.xml", Level = "EN 16931" },
            ["zugferd-xrechnung"] = new FormatInfo { Gid = Codes.XRECHNUNG_ID, IsPdf = true, FileName = "xrechnung.xml", Level = "XRECHNUNG" },
        };

        public static readonly Dictionary<string, string> ALIASES = new Dictionary<string, string>
        {
            ["facturx"] = "factur-x", ["zugferd-en16931"] = "zugferd", ["factur-x-en16931"] = "factur-x", ["cii-xrechnung"] = "xrechnung-cii",
            ["xrechnung_cii"] = "xrechnung-cii", ["en16931-cii"] = "cii", ["zugferd-comfort"] = "zugferd",
        };

        public static string NormFormat(object? fmt)
        {
            var f = Py.Lower(Py.Trim(Py.Str(Py.T(fmt) ? fmt : "")));
            return ALIASES.TryGetValue(f, out var a) ? a : f;
        }

        private static string Date102(object? d, string field)
        {
            if (!Codes.IsDate(d)) throw new InputError(field + " must be YYYY-MM-DD");
            return Py.Str(d).Replace("-", "");
        }

        private static string AcXml(Ac ac, bool doc)
        {
            var s = "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>" + (ac.Charge ? "true" : "false") + "</udt:Indicator></ram:ChargeIndicator>";
            if (ac.Percent != null) s += "<ram:CalculationPercent>" + ac.Percent.Num() + "</ram:CalculationPercent>";
            if (ac.Base != null) s += "<ram:BasisAmount>" + ac.Base.A() + "</ram:BasisAmount>";
            s += "<ram:ActualAmount>" + ac.Amount.A() + "</ram:ActualAmount>";
            if (Py.T(ac.ReasonCode)) s += "<ram:ReasonCode>" + Py.X(ac.ReasonCode) + "</ram:ReasonCode>";
            if (Py.T(ac.Reason)) s += "<ram:Reason>" + Py.X(ac.Reason) + "</ram:Reason>";
            if (doc)
            {
                s += "<ram:CategoryTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>" + Py.X(ac.Cat) + "</ram:CategoryCode>";
                if (ac.Cat != "O") s += "<ram:RateApplicablePercent>" + ac.Rate.Num() + "</ram:RateApplicablePercent>";
                s += "</ram:CategoryTradeTax>";
            }
            return s + "</ram:SpecifiedTradeAllowanceCharge>";
        }

        private static string Party(object? p, string role, bool xr)
        {
            foreach (var f in new[] { "name", "country" })
                if (!Py.T(Py.G(p, f))) throw new InputError($"{role}.{f} is required");
            var cc = Py.Upper(Py.G(p, "country"));
            var tag = role == "seller" ? "SellerTradeParty" : "BuyerTradeParty";
            var s = new StringBuilder($"<ram:{tag}>");
            s.Append("<ram:Name>" + Py.X(Py.G(p, "name")) + "</ram:Name>");
            if (Py.T(Py.G(p, "legal_form")) && role == "seller") s.Append("<ram:Description>" + Py.X(Py.G(p, "legal_form")) + "</ram:Description>");
            if (Py.T(Py.G(p, "company_id")) || Py.T(Py.G(p, "trading_name")))
            {
                s.Append("<ram:SpecifiedLegalOrganization>");
                if (Py.T(Py.G(p, "company_id")))
                {
                    var sch = Py.T(Py.G(p, "company_id_scheme")) ? " schemeID=\"" + Py.X(Py.G(p, "company_id_scheme")) + "\"" : "";
                    s.Append($"<ram:ID{sch}>" + Py.X(Py.G(p, "company_id")) + "</ram:ID>");
                }
                if (Py.T(Py.G(p, "trading_name"))) s.Append("<ram:TradingBusinessName>" + Py.X(Py.G(p, "trading_name")) + "</ram:TradingBusinessName>");
                s.Append("</ram:SpecifiedLegalOrganization>");
            }
            if (Py.T(Py.G(p, "contact_name")) || Py.T(Py.G(p, "phone")) || Py.T(Py.G(p, "email")))
            {
                s.Append("<ram:DefinedTradeContact>");
                if (Py.T(Py.G(p, "contact_name"))) s.Append("<ram:PersonName>" + Py.X(Py.G(p, "contact_name")) + "</ram:PersonName>");
                if (Py.T(Py.G(p, "phone"))) s.Append("<ram:TelephoneUniversalCommunication><ram:CompleteNumber>" + Py.X(Py.G(p, "phone")) + "</ram:CompleteNumber></ram:TelephoneUniversalCommunication>");
                if (Py.T(Py.G(p, "email"))) s.Append("<ram:EmailURIUniversalCommunication><ram:URIID>" + Py.X(Py.G(p, "email")) + "</ram:URIID></ram:EmailURIUniversalCommunication>");
                s.Append("</ram:DefinedTradeContact>");
            }
            s.Append("<ram:PostalTradeAddress>");
            if (Py.T(Py.G(p, "postal_code"))) s.Append("<ram:PostcodeCode>" + Py.X(Py.G(p, "postal_code")) + "</ram:PostcodeCode>");
            if (Py.T(Py.G(p, "street"))) s.Append("<ram:LineOne>" + Py.X(Py.G(p, "street")) + "</ram:LineOne>");
            if (Py.T(Py.G(p, "city"))) s.Append("<ram:CityName>" + Py.X(Py.G(p, "city")) + "</ram:CityName>");
            s.Append("<ram:CountryID>" + Py.X(cc) + "</ram:CountryID></ram:PostalTradeAddress>");
            var (eid, esch) = Codes.Endpoint(p, xr ? "xrechnung" : "en16931", role);
            if (Py.T(eid)) s.Append("<ram:URIUniversalCommunication><ram:URIID schemeID=\"" + Py.X(esch) + "\">" + Py.X(eid) + "</ram:URIID></ram:URIUniversalCommunication>");
            if (Py.T(Py.G(p, "vat_id"))) s.Append("<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">" + Py.X(Py.Upper(Py.Str(Py.G(p, "vat_id")).Replace(" ", ""))) + "</ram:ID></ram:SpecifiedTaxRegistration>");
            if (Py.T(Py.G(p, "tax_number"))) s.Append("<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"FC\">" + Py.X(Py.G(p, "tax_number")) + "</ram:ID></ram:SpecifiedTaxRegistration>");
            return s + $"</ram:{tag}>";
        }

        public static (string Xml, Dictionary<string, string> Totals, Money Model) Build(object? j, string fmt)
        {
            if (!FORMATS.ContainsKey(fmt)) throw new InputError("format must be one of " + string.Join(", ", FORMATS.Keys));
            var gid = FORMATS[fmt].Gid;
            var xr = gid == Codes.XRECHNUNG_ID;
            var m = MoneyModel.Compute(j);
            var cur = m.Cur;
            var isCn = m.IsCn;
            var tc = Py.Str(Py.T(Py.G(j, "type_code")) ? Py.G(j, "type_code") : (isCn ? "381" : "380"));
            var x = new StringBuilder();
            x.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rsm:CrossIndustryInvoice " + string.Join(" ", CII_NS.Select(n => $"xmlns:{n.Prefix}=\"{n.Ns}\"")) + ">");
            x.Append("<rsm:ExchangedDocumentContext>");
            if (xr) x.Append("<ram:BusinessProcessSpecifiedDocumentContextParameter><ram:ID>" + Codes.PEPPOL_PROFILE + "</ram:ID></ram:BusinessProcessSpecifiedDocumentContextParameter>");
            x.Append($"<ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>{gid}</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter></rsm:ExchangedDocumentContext>");
            x.Append("<rsm:ExchangedDocument><ram:ID>" + Py.X(Py.G(j, "invoice_number")) + "</ram:ID><ram:TypeCode>" + Py.X(tc) + "</ram:TypeCode>"
                + "<ram:IssueDateTime><udt:DateTimeString format=\"102\">" + Date102(Py.G(j, "issue_date"), "issue_date") + "</udt:DateTimeString></ram:IssueDateTime>");
            if (Py.T(Py.G(j, "note"))) x.Append("<ram:IncludedNote><ram:Content>" + Py.X(Py.G(j, "note")) + "</ram:Content></ram:IncludedNote>");
            x.Append("</rsm:ExchangedDocument><rsm:SupplyChainTradeTransaction>");
            foreach (var ln in m.Lines)
            {
                var s = $"<ram:IncludedSupplyChainTradeLineItem><ram:AssociatedDocumentLineDocument><ram:LineID>{ln.Id}</ram:LineID>"
                    + "</ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct>";
                if (Py.T(ln.Sid)) s += "<ram:SellerAssignedID>" + Py.X(ln.Sid) + "</ram:SellerAssignedID>";
                s += "<ram:Name>" + Py.X(ln.Name) + "</ram:Name>";
                if (Py.T(ln.Desc)) s += "<ram:Description>" + Py.X(ln.Desc) + "</ram:Description>";
                s += "</ram:SpecifiedTradeProduct><ram:SpecifiedLineTradeAgreement><ram:NetPriceProductTradePrice>"
                    + "<ram:ChargeAmount>" + ln.Price.Num() + "</ram:ChargeAmount></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement>"
                    + "<ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode=\"" + Py.X(ln.Unit) + "\">" + ln.Q.Num() + "</ram:BilledQuantity>"
                    + "</ram:SpecifiedLineTradeDelivery><ram:SpecifiedLineTradeSettlement><ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode>"
                    + "<ram:CategoryCode>" + Py.X(ln.Cat) + "</ram:CategoryCode>";
                if (ln.Cat != "O") s += "<ram:RateApplicablePercent>" + ln.Rate.Num() + "</ram:RateApplicablePercent>";
                s += "</ram:ApplicableTradeTax>" + string.Concat(ln.Acs.Select(a => AcXml(a, false)));
                s += "<ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>" + ln.Net.A() + "</ram:LineTotalAmount>"
                    + "</ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement></ram:IncludedSupplyChainTradeLineItem>";
                x.Append(s);
            }
            x.Append("<ram:ApplicableHeaderTradeAgreement>");
            if (Py.T(Py.G(j, "buyer_reference"))) x.Append("<ram:BuyerReference>" + Py.X(Py.G(j, "buyer_reference")) + "</ram:BuyerReference>");
            x.Append(Party(Py.G(j, "seller"), "seller", xr) + Party(Py.G(j, "buyer"), "buyer", xr));
            if (Py.T(Py.G(j, "order_reference")))
                x.Append("<ram:BuyerOrderReferencedDocument><ram:IssuerAssignedID>" + Py.X(Py.G(j, "order_reference")) + "</ram:IssuerAssignedID></ram:BuyerOrderReferencedDocument>");
            x.Append("</ram:ApplicableHeaderTradeAgreement><ram:ApplicableHeaderTradeDelivery>");
            if (Py.T(Py.G(j, "delivery_country")))
                x.Append("<ram:ShipToTradeParty><ram:PostalTradeAddress><ram:CountryID>" + Py.X(Py.Upper(Py.G(j, "delivery_country"))) + "</ram:CountryID>"
                    + "</ram:PostalTradeAddress></ram:ShipToTradeParty>");
            if (Py.T(Py.G(j, "delivery_date")))
                x.Append("<ram:ActualDeliverySupplyChainEvent><ram:OccurrenceDateTime><udt:DateTimeString format=\"102\">"
                    + Date102(Py.G(j, "delivery_date"), "delivery_date") + "</udt:DateTimeString></ram:OccurrenceDateTime></ram:ActualDeliverySupplyChainEvent>");
            x.Append("</ram:ApplicableHeaderTradeDelivery><ram:ApplicableHeaderTradeSettlement>");
            var pay = Py.T(Py.G(j, "payment")) ? Py.G(j, "payment") : new List<object?>();
            if (!Py.IsDict(pay)) throw new InputError("payment must be an object");
            if (Py.T(Py.G(pay, "reference"))) x.Append("<ram:PaymentReference>" + Py.X(Py.G(pay, "reference")) + "</ram:PaymentReference>");
            x.Append($"<ram:InvoiceCurrencyCode>{cur}</ram:InvoiceCurrencyCode>");
            if (Py.T(Py.G(pay, "iban")) || Py.T(Py.G(pay, "means_code")))
            {
                var code = Py.Has(pay, "means_code") ? Py.G(pay, "means_code") : (Py.T(Py.G(pay, "iban")) ? "58" : "1");
                var pm = "<ram:SpecifiedTradeSettlementPaymentMeans><ram:TypeCode>" + Py.X(code) + "</ram:TypeCode>";
                if (Py.T(Py.G(pay, "iban")))
                {
                    pm += "<ram:PayeePartyCreditorFinancialAccount><ram:IBANID>" + Py.X(Py.Str(Py.G(pay, "iban")).Replace(" ", "")) + "</ram:IBANID>";
                    if (Py.T(Py.G(pay, "account_name"))) pm += "<ram:AccountName>" + Py.X(Py.G(pay, "account_name")) + "</ram:AccountName>";
                    pm += "</ram:PayeePartyCreditorFinancialAccount>";
                    if (Py.T(Py.G(pay, "bic"))) pm += "<ram:PayeeSpecifiedCreditorFinancialInstitution><ram:BICID>" + Py.X(Py.G(pay, "bic")) + "</ram:BICID></ram:PayeeSpecifiedCreditorFinancialInstitution>";
                }
                x.Append(pm + "</ram:SpecifiedTradeSettlementPaymentMeans>");
            }
            var tpd = Py.T(Py.G(j, "tax_point_date")) ? Date102(Py.G(j, "tax_point_date"), "tax_point_date") : null;
            for (int gi = 0; gi < m.Vat.Count; gi++)
            {
                var g = m.Vat[gi];
                var s = "<ram:ApplicableTradeTax><ram:CalculatedAmount>" + g.Tax.A() + "</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode>";
                if (Py.T(g.ExText)) s += "<ram:ExemptionReason>" + Py.X(g.ExText) + "</ram:ExemptionReason>";
                s += "<ram:BasisAmount>" + g.Base.A() + "</ram:BasisAmount><ram:CategoryCode>" + Py.X(g.Cat) + "</ram:CategoryCode>";
                if (Py.T(g.ExCode)) s += "<ram:ExemptionReasonCode>" + Py.X(g.ExCode) + "</ram:ExemptionReasonCode>";
                if (tpd != null && gi == 0) s += "<ram:TaxPointDate><udt:DateString format=\"102\">" + tpd + "</udt:DateString></ram:TaxPointDate>";
                if (g.Cat != "O") s += "<ram:RateApplicablePercent>" + g.Rate.Num() + "</ram:RateApplicablePercent>";
                x.Append(s + "</ram:ApplicableTradeTax>");
            }
            foreach (var a in m.DocAcs) x.Append(AcXml(a, true));
            var due = !isCn ? Py.G(j, "due_date") : null;
            if (Py.T(Py.G(pay, "terms")) || Py.T(due))
            {
                var s = "<ram:SpecifiedTradePaymentTerms>";
                if (Py.T(Py.G(pay, "terms"))) s += "<ram:Description>" + Py.X(Py.G(pay, "terms")) + "</ram:Description>";
                if (Py.T(due)) s += "<ram:DueDateDateTime><udt:DateTimeString format=\"102\">" + Date102(due, "due_date") + "</udt:DateTimeString></ram:DueDateDateTime>";
                x.Append(s + "</ram:SpecifiedTradePaymentTerms>");
            }
            var sm = "<ram:SpecifiedTradeSettlementHeaderMonetarySummation><ram:LineTotalAmount>" + m.LineTotal.A() + "</ram:LineTotalAmount>";
            if (m.DocAcs.Count > 0) sm += "<ram:ChargeTotalAmount>" + m.ChargeTotal.A() + "</ram:ChargeTotalAmount><ram:AllowanceTotalAmount>" + m.AllowTotal.A() + "</ram:AllowanceTotalAmount>";
            sm += "<ram:TaxBasisTotalAmount>" + m.Basis.A() + "</ram:TaxBasisTotalAmount>"
                + $"<ram:TaxTotalAmount currencyID=\"{cur}\">" + m.VatTotal.A() + "</ram:TaxTotalAmount>"
                + "<ram:GrandTotalAmount>" + m.Incl.A() + "</ram:GrandTotalAmount>";
            if (!m.Prepaid.IsZero) sm += "<ram:TotalPrepaidAmount>" + m.Prepaid.A() + "</ram:TotalPrepaidAmount>";
            sm += "<ram:DuePayableAmount>" + m.Payable.A() + "</ram:DuePayableAmount></ram:SpecifiedTradeSettlementHeaderMonetarySummation>";
            x.Append(sm);
            if (Py.T(Py.G(j, "preceding_invoice")))
            {
                var s = "<ram:InvoiceReferencedDocument><ram:IssuerAssignedID>" + Py.X(Py.G(j, "preceding_invoice")) + "</ram:IssuerAssignedID>";
                if (Py.T(Py.G(j, "preceding_invoice_date")))
                    s += "<ram:FormattedIssueDateTime><qdt:DateTimeString format=\"102\">" + Date102(Py.G(j, "preceding_invoice_date"), "preceding_invoice_date")
                        + "</qdt:DateTimeString></ram:FormattedIssueDateTime>";
                x.Append(s + "</ram:InvoiceReferencedDocument>");
            }
            x.Append("</ram:ApplicableHeaderTradeSettlement></rsm:SupplyChainTradeTransaction></rsm:CrossIndustryInvoice>");
            return (x.ToString(), MoneyModel.Totals(m), m);
        }
    }
}
