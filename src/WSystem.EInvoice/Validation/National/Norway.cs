using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace WSystem.EInvoice.Validation.National
{
    /// <summary>
    /// Norwegian national field checks. W System's own checks from Norwegian law and the Brønnøysund number rules,
    /// NOT the official EHF / Peppol BIS (NO-R) schematron. Warnings only (ruleset NO-LAW).
    /// </summary>
    public static class Norway
    {
        public const string VERSION = "W System NO field checks 2026-10-21";
        public const string SOURCE = "https://wsystem.dev/norway-e-invoicing-2027-software";
        private static readonly string[] RATES = { "25", "15", "12" };
        private static readonly int[] WEIGHTS = { 3, 2, 7, 6, 5, 4, 3, 2 };
        private static readonly Regex Nine = new Regex(@"^[0-9]{9}$", RegexOptions.CultureInvariant);

        /// <summary>Brønnøysund modulus-11 check of a 9-digit organisation number.</summary>
        public static bool OrgnrOk(string n)
        {
            if (!Nine.IsMatch(n)) return false;
            int s = 0;
            for (int i = 0; i < WEIGHTS.Length; i++) s += (n[i] - '0') * WEIGHTS[i];
            int r = 11 - s % 11;
            r = r == 11 ? 0 : r;
            return r != 10 && r == n[8] - '0';
        }

        private static ValidationIssue W(string rid, string msg, string[] bts, string fix) => NatXml.W("NO-LAW", rid, msg, bts, fix);

        private static (string, List<string>) TaxIds(NatXml x, XmlElement? party)
        {
            var vat = ""; var other = new List<string>();
            foreach (var pts in x.FindAll(party, "cac:PartyTaxScheme"))
            {
                var cid = x.T(pts, "cbc:CompanyID");
                if (NatXml.Up(x.T(pts, "cac:TaxScheme/cbc:ID")) == "VAT") vat = NatXml.Up(cid.Replace(" ", ""));
                else other.Add(cid);
            }
            return (vat, other);
        }

        private static readonly Regex NoVat = new Regex(@"^NO[0-9]{9}(MVA)?$", RegexOptions.CultureInvariant);
        private static readonly Regex NoVatMva = new Regex(@"^NO[0-9]{9}MVA$", RegexOptions.CultureInvariant);
        private static readonly Regex AsName = new Regex(@"\b(ASA|AS|NUF)\s*$", RegexOptions.ECMAScript);
        private static readonly Regex AsForm = new Regex(@"\b(ASA|AS|NUF)\b", RegexOptions.ECMAScript);

        internal static (List<ValidationIssue>, Dictionary<string, object?>) Check(Document doc)
        {
            var x = new NatXml(Document.Ns(doc.Dom));
            var root = doc.Dom.DocumentElement!;
            if (doc.Syntax != "ubl")
            {
                if (NatXml.Up(x.T(root, ".//ram:SellerTradeParty/ram:PostalTradeAddress/ram:CountryID")) == "NO")
                    return (new List<ValidationIssue>(), new Dictionary<string, object?> { ["applies"] = false, ["note"] = "Norwegian field checks run on UBL only (Norwegian e-invoices over Peppol use EHF / Peppol BIS Billing 3.0 in UBL 2.1); this CII file was checked against EN 16931 only." });
                return (new List<ValidationIssue>(), new Dictionary<string, object?> { ["applies"] = false });
            }
            var sup = x.Find(root, "cac:AccountingSupplierParty/cac:Party");
            var cus = x.Find(root, "cac:AccountingCustomerParty/cac:Party");
            if (NatXml.Up(x.T(sup, "cac:PostalAddress/cac:Country/cbc:IdentificationCode")) != "NO")
                return (new List<ValidationIssue>(), new Dictionary<string, object?> { ["applies"] = false });
            var buyerCc = NatXml.Up(x.T(cus, "cac:PostalAddress/cac:Country/cbc:IdentificationCode"));
            var o = new List<ValidationIssue>();
            if (!x.T(root, "cbc:CustomizationID").Contains("peppol.eu:2017:poacc:billing:3.0"))
                o.Add(W("NO-LAW-01", "Norwegian e-invoices are exchanged over Peppol as EHF / Peppol BIS Billing 3.0 (UBL 2.1); this file's CustomizationID (BT-24) is not the Peppol BIS 3 identifier.",
                    new[] { "BT-24" }, "Use CustomizationID urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0 (create with format=peppol-bis3)."));
            var (sVat, sOther) = TaxIds(x, sup);
            var sLegal = x.T(sup, "cac:PartyLegalEntity/cbc:CompanyID").Replace(" ", "");
            var sOrg = sLegal != "" ? sLegal : (NoVat.IsMatch(sVat) ? sVat.Substring(2, 9) : "");
            if (sLegal == "")
                o.Add(W("NO-LAW-02", "Seller legal registration identifier (BT-30) is empty. A Norwegian sales document states the seller's organisation number.",
                    new[] { "BT-30", "BT-30-1" }, "Add the 9-digit organisation number in cac:PartyLegalEntity/cbc:CompanyID schemeID=\"0192\" (create: seller.company_id)."));
            if (sOrg != "" && !OrgnrOk(sOrg))
                o.Add(W("NO-LAW-03", $"Seller organisation number {sOrg} fails the Brønnøysund modulus-11 check digit, so it can't be a real organisation number.",
                    new[] { "BT-30", "BT-31" }, "Check the number at https://data.brreg.no/enhetsregisteret/oppslag/enheter or GET /company."));
            if (sVat != "" && sVat.StartsWith("NO", System.StringComparison.Ordinal) && !NoVatMva.IsMatch(sVat))
                o.Add(W("NO-LAW-04", $"Seller VAT identifier {sVat}: a seller registered in the Norwegian VAT register shows the organisation number followed by 'MVA' (format NO + 9 digits + MVA).",
                    new[] { "BT-31" }, "Use e.g. NO923609016MVA in the VAT PartyTaxScheme."));
            var legalForm = x.T(sup, "cac:PartyLegalEntity/cbc:CompanyLegalForm");
            var name = x.T(sup, "cac:PartyLegalEntity/cbc:RegistrationName");
            if (name == "") name = x.T(sup, "cac:PartyName/cbc:Name");
            var looksAs = AsName.IsMatch(name) || AsForm.IsMatch(legalForm);
            var hasFr = sOther.Concat(new[] { legalForm }).Any(s => s.ToLowerInvariant().Contains("foretaksregisteret"));
            if (looksAs && !hasFr)
                o.Add(W("NO-LAW-05", $"Seller '{name}' looks like an AS, ASA or NUF, but the word 'Foretaksregisteret' is not on the invoice (foretaksregisterloven § 10-2).",
                    new[] { "BT-32", "BT-33" }, "Add a PartyTaxScheme with CompanyID 'Foretaksregisteret' and TaxScheme ID 'TAX' (create does this when seller.legal_form is AS/ASA/NUF)."));
            var ep = x.Find(sup, "cbc:EndpointID");
            if (ep != null && Document.PyStrip(ep.GetAttribute("schemeID")) == "0192")
            {
                var eid = Document.PyStrip(ep.InnerText);
                if (!OrgnrOk(eid))
                    o.Add(W("NO-LAW-06", $"Seller electronic address 0192:{eid} is not a valid 9-digit organisation number; Norwegian Peppol participants are 0192:<org. no.>.",
                        new[] { "BT-34", "BT-34-1" }, "Use the organisation number only (no NO prefix, no MVA suffix)."));
            }
            if (buyerCc == "NO")
            {
                var bLegal = x.T(cus, "cac:PartyLegalEntity/cbc:CompanyID").Replace(" ", "");
                if (bLegal == "")
                    o.Add(W("NO-LAW-07", "Buyer organisation number (BT-47) is empty. From 2028 Norwegian sales documents to business buyers must state the buyer's organisation number, and it is how the buyer is found in Peppol (0192).",
                        new[] { "BT-47", "BT-47-1" }, "Add the buyer's organisation number in cac:PartyLegalEntity/cbc:CompanyID schemeID=\"0192\" (create: buyer.company_id)."));
                else if (!OrgnrOk(bLegal))
                    o.Add(W("NO-LAW-08", $"Buyer organisation number {bLegal} fails the modulus-11 check digit.", new[] { "BT-47" }, "Check the buyer's number in the Brønnøysund register."));
                var bep = x.Find(cus, "cbc:EndpointID");
                if (bep != null && Document.PyStrip(bep.GetAttribute("schemeID")) == "0192" && !OrgnrOk(Document.PyStrip(bep.InnerText)))
                    o.Add(W("NO-LAW-09", "Buyer electronic address 0192:" + Document.PyStrip(bep.InnerText) + " is not a valid organisation number.",
                        new[] { "BT-49", "BT-49-1" }, "Look the buyer up with GET /einvoice/route before sending."));
                foreach (var st in x.FindAll(root, "cac:TaxTotal/cac:TaxSubtotal"))
                {
                    var cat = NatXml.Up(x.T(st, "cac:TaxCategory/cbc:ID"));
                    var p = x.T(st, "cac:TaxCategory/cbc:Percent");
                    var rate = NatXml.Num(p != "" ? p : "0");
                    if (cat == "S" && !RATES.Contains(rate))
                        o.Add(W("NO-LAW-10", $"Standard-rated (S) VAT breakdown uses {rate} %; Norwegian rates are 25 %, 15 % (food) and 12 %.",
                            new[] { "BT-119", "BT-118" }, "Check the VAT rate for this domestic supply."));
                }
            }
            bool? ok = sOrg != "" ? OrgnrOk(sOrg) : (bool?)null;
            return (o, new Dictionary<string, object?>
            {
                ["applies"] = true, ["version"] = VERSION, ["source"] = SOURCE,
                ["checks"] = "W System field checks from Norwegian law and the Brønnøysund number rules; not the official EHF / Peppol BIS (NO-R) schematron.",
                ["seller_org_number"] = sOrg != "" ? sOrg : null, ["seller_org_number_valid"] = ok,
                ["participant_id"] = sOrg != "" && ok == true ? "0192:" + sOrg : null,
            });
        }
    }
}
