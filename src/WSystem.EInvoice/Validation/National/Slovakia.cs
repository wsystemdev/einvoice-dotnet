using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WSystem.EInvoice.Validation.National
{
    /// <summary>
    /// Slovak national field checks (eFaktúra, from 2027-01-01). W System's own checks from public Financial
    /// Administration (FR SR) documents, NOT the official Peppol BIS / Slovak schematron. Warnings only (SK-FS).
    /// </summary>
    public static class Slovakia
    {
        public const string TRANSPOSITION = "FR SR Peppol BIS transposition v1.13 (2026-10-29)";
        public const string SOURCE = "https://www.financnasprava.sk/sk/podnikatelia/dane/dan-z-pridanej-hodnoty/e-faktura";
        public const string UUID_NAMESPACE = "3fd00464-9f15-52ce-8cdc-6dd374d5ad42";
        private static readonly string[] RATES = { "23", "19", "5" };
        private static readonly string[] EXEMPT_CATS = { "E", "AE", "K", "G", "O" };

        /// <summary>UUIDv5 of 'BT-34@schemeID BT-34 BT-3 BT-1 BT-2' (single spaces, UTF-8) in the fixed FR SR namespace.</summary>
        public static string InvoiceUuid(string scheme, string endpoint, string typeCode, string number, string issueDate)
        {
            var name = string.Join(" ", new[] { scheme, endpoint, typeCode, number, issueDate }.Select(Document.PyStrip));
            var nsHex = UUID_NAMESPACE.Replace("-", "");
            var nsBytes = Enumerable.Range(0, 16).Select(i => Convert.ToByte(nsHex.Substring(i * 2, 2), 16)).ToArray();
            byte[] hash;
            using (var sha = SHA1.Create()) hash = sha.ComputeHash(nsBytes.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
            var h = string.Concat(hash.Select(b => b.ToString("x2"))).ToCharArray();
            h[12] = '5';
            h[16] = ((Convert.ToInt32(h[16].ToString(), 16) & 0x3) | 0x8).ToString("x")[0];
            var s = new string(h);
            return s.Substring(0, 8) + "-" + s.Substring(8, 4) + "-" + s.Substring(12, 4) + "-" + s.Substring(16, 4) + "-" + s.Substring(20, 12);
        }

        private static ValidationIssue W(string rid, string msg, string[] bts, string fix) => NatXml.W("SK-FS", rid, msg, bts, fix);

        private static readonly Regex Dic = new Regex(@"^SK[0-9]{10}$", RegexOptions.CultureInvariant);
        private static readonly Regex Ten = new Regex(@"^[0-9]{10}$", RegexOptions.CultureInvariant);

        internal static (List<ValidationIssue>, Dictionary<string, object?>) Check(Document doc)
        {
            var x = new NatXml(Document.Ns(doc.Dom));
            var root = doc.Dom.DocumentElement!;
            if (doc.Syntax != "ubl")
            {
                if (NatXml.Up(x.T(root, ".//ram:SellerTradeParty/ram:PostalTradeAddress/ram:CountryID")) == "SK")
                    return (new List<ValidationIssue>(), new Dictionary<string, object?> { ["applies"] = false, ["note"] = "Slovak national field checks run on UBL only (Slovak eFaktúra over Peppol uses UBL 2.1 / Peppol BIS Billing 3.0); this CII file was checked against EN 16931 only." });
                return (new List<ValidationIssue>(), new Dictionary<string, object?> { ["applies"] = false });
            }
            var sup = x.Find(root, "cac:AccountingSupplierParty/cac:Party");
            var cus = x.Find(root, "cac:AccountingCustomerParty/cac:Party");
            if (NatXml.Up(x.T(sup, "cac:PostalAddress/cac:Country/cbc:IdentificationCode")) != "SK")
                return (new List<ValidationIssue>(), new Dictionary<string, object?> { ["applies"] = false });
            var buyerCc = NatXml.Up(x.T(cus, "cac:PostalAddress/cac:Country/cbc:IdentificationCode"));
            var isCn = root.LocalName == "CreditNote";
            var o = new List<ValidationIssue>();
            if (!x.T(root, "cbc:CustomizationID").Contains("peppol.eu:2017:poacc:billing:3.0"))
                o.Add(W("SK-FS-01", "Slovak eFaktúra is exchanged over Peppol as Peppol BIS Billing 3.0 (UBL 2.1); this file's CustomizationID (BT-24) is not the Peppol BIS 3 identifier.",
                    new[] { "BT-24" }, "Use CustomizationID urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0 and ProfileID urn:fdc:peppol.eu:2017:poacc:billing:01:1.0 (create with format=peppol-bis3)."));
            var vat = "";
            foreach (var pts in x.FindAll(sup, "cac:PartyTaxScheme"))
                if (NatXml.Up(x.T(pts, "cac:TaxScheme/cbc:ID")) == "VAT") vat = NatXml.Up(x.T(pts, "cbc:CompanyID").Replace(" ", ""));
            var dic = Dic.IsMatch(vat) ? vat.Substring(2) : "";
            var ep = x.Find(sup, "cbc:EndpointID");
            var (epId, epScheme) = ep != null ? (Document.PyStrip(ep.InnerText), Document.PyStrip(ep.GetAttribute("schemeID"))) : ("", "");
            if (epId == "")
                o.Add(W("SK-FS-02", "Seller electronic address (BT-34) is mandatory in the Slovak transposition (1..1); it also feeds the invoice UUID.",
                    new[] { "BT-34", "BT-34-1" }, "Add cbc:EndpointID schemeID=\"0245\" with the seller's 10-digit DIČ (the digits after 'SK' in the IČ DPH)."));
            else if (epScheme != "0245" || !Ten.IsMatch(epId))
                o.Add(W("SK-FS-03", "Seller electronic address is " + (epScheme != "" ? epScheme : "?") + $":{epId}; Slovak participants are registered as 0245:<10-digit DIČ>.",
                    new[] { "BT-34", "BT-34-1" }, "Use schemeID 0245 and the 10-digit DIČ (not 9950 + IČ DPH)."));
            else if (dic != "" && epId != dic)
                o.Add(W("SK-FS-04", $"Seller endpoint 0245:{epId} does not match the DIČ in the seller VAT number {vat}.",
                    new[] { "BT-34", "BT-31" }, "The Slovak IČ DPH is 'SK' + DIČ; the EndpointID should normally be the same 10 digits."));
            var bep = x.Find(cus, "cbc:EndpointID");
            var (bId, bScheme) = bep != null ? (Document.PyStrip(bep.InnerText), Document.PyStrip(bep.GetAttribute("schemeID"))) : ("", "");
            if (bId == "")
                o.Add(W("SK-FS-05", "Buyer electronic address (BT-49) is mandatory in the Slovak transposition (1..1).",
                    new[] { "BT-49", "BT-49-1" }, "Add the buyer's cbc:EndpointID; for a Slovak buyer schemeID=\"0245\" and the 10-digit DIČ. Check it on Peppol (GET /einvoice/route)."));
            else if (buyerCc == "SK" && (bScheme != "0245" || !Ten.IsMatch(bId)))
                o.Add(W("SK-FS-06", "Buyer electronic address is " + (bScheme != "" ? bScheme : "?") + $":{bId}; Slovak buyers are addressed as 0245:<10-digit DIČ>.",
                    new[] { "BT-49", "BT-49-1" }, "Use schemeID 0245 and the buyer's 10-digit DIČ."));
            var paths = new[] { "cac:PostalAddress/cbc:StreetName", "cac:PostalAddress/cbc:CityName", "cac:PostalAddress/cbc:PostalZone" };
            foreach (var (party, role, bts) in new[] { (sup, "Seller", new[] { "BT-35", "BT-37", "BT-38" }), (cus, "Buyer", new[] { "BT-50", "BT-52", "BT-53" }) })
            {
                var missing = new List<string>();
                for (int i = 0; i < bts.Length; i++) if (x.T(party, paths[i]) == "") missing.Add(bts[i]);
                if (missing.Count > 0)
                    o.Add(W(role == "Seller" ? "SK-FS-07" : "SK-FS-08",
                        $"{role} address is incomplete (" + string.Join(", ", missing) + " missing). EN 16931 makes these optional, but the Slovak VAT Act (s. 74(1)(" + (role == "Seller" ? "a" : "b") + ") requires the address on the invoice.",
                        missing.ToArray(), "Fill " + role.ToLowerInvariant() + " street, city and postal code."));
            }
            if (x.T(sup, "cac:PartyLegalEntity/cbc:CompanyID") == "")
                o.Add(W("SK-FS-09", "Seller legal registration identifier (BT-30, the IČO) is conditionally required: give it if an IČO has been assigned.",
                    new[] { "BT-30", "BT-30-1" }, "Add the seller IČO in cac:PartyLegalEntity/cbc:CompanyID (with schemeID from the ICD list)."));
            if (x.T(sup, "cac:PartyLegalEntity/cbc:CompanyLegalForm") == "")
                o.Add(W("SK-FS-10", "Seller additional legal information (BT-33) is conditionally required: legal form and the register entry (court, section, insert number) for registered businesses (Commercial Code s. 3a).",
                    new[] { "BT-33" }, "Example: 'Spoločnosť s ručením obmedzeným, zapísaná v Obchodnom registri Okresného súdu Žilina, oddiel Sro, vložka č. 12345/L' in cbc:CompanyLegalForm."));
            if (buyerCc == "SK" && x.T(cus, "cac:PartyLegalEntity/cbc:CompanyID") == "")
                o.Add(W("SK-FS-11", "Buyer legal registration identifier (BT-47, the IČO) is conditionally required if an IČO has been assigned.",
                    new[] { "BT-47", "BT-47-1" }, "Add the buyer IČO in cac:PartyLegalEntity/cbc:CompanyID if it has one."));
            var issue = x.T(root, "cbc:IssueDate");
            var taxPoint = x.T(root, "cbc:TaxPointDate");
            if (x.Find(root, "cac:InvoicePeriod/cbc:DescriptionCode") != null)
                o.Add(W("SK-FS-12", "The Slovak transposition does not use BT-8 (VAT point date code); the tax point date goes in BT-7.",
                    new[] { "BT-8", "BT-7" }, "Remove cac:InvoicePeriod/cbc:DescriptionCode and use cbc:TaxPointDate."));
            var delivery = x.T(root, "cac:Delivery/cbc:ActualDeliveryDate");
            if (taxPoint == "" && delivery != "" && delivery != issue)
                o.Add(W("SK-FS-13", $"Delivery date {delivery} differs from the issue date {issue} but BT-7 (tax point date, DUZP) is empty. In Slovakia BT-7 is required when it is known and differs from the issue date (VAT Act s. 74(1)(d)).",
                    new[] { "BT-7", "BT-72" }, "Add cbc:TaxPointDate (create: tax_point_date)."));
            foreach (var st in x.FindAll(root, "cac:TaxTotal/cac:TaxSubtotal"))
            {
                var cat = NatXml.Up(x.T(st, "cac:TaxCategory/cbc:ID"));
                var p = x.T(st, "cac:TaxCategory/cbc:Percent");
                var rate = NatXml.Num(p != "" ? p : "0");
                if (cat == "S" && !RATES.Contains(rate) && buyerCc == "SK")
                    o.Add(W("SK-FS-14", $"Standard-rated (S) VAT breakdown uses {rate} %; Slovak rates are 23 %, 19 % and 5 %.",
                        new[] { "BT-119", "BT-118" }, "Check the VAT rate for this domestic supply."));
                if (EXEMPT_CATS.Contains(cat))
                {
                    if (x.T(st, "cac:TaxCategory/cbc:TaxExemptionReasonCode") == "")
                        o.Add(W("SK-FS-15", $"VAT category {cat} has no VATEX exemption reason code (BT-121). FR SR recommends using BT-121 primarily, with BT-120 text as the explanation (FAQ part III mapping).",
                            new[] { "BT-121", "BT-120" }, "E.g. VATEX-EU-IC for s. 43 intra-EU supply (K), VATEX-EU-G for exports (G), VATEX-EU-AE for reverse charge (AE), VATEX-EU-O for not subject (O), VATEX-EU-132-1x / VATEX-EU-135-1 for s. 28-41 exemptions (E)."));
                    if (x.T(st, "cac:TaxCategory/cbc:TaxExemptionReason") == "")
                        o.Add(W("SK-FS-16", $"VAT category {cat} has no exemption reason text (BT-120). It is conditionally required where the VAT Act asks for a specific wording (s. 74(1)(h), (k)-(n), e.g. 'prenesenie daňovej povinnosti' for reverse charge).",
                            new[] { "BT-120" }, "Add cbc:TaxExemptionReason with the legal wording / reference."));
                }
            }
            var typeCode = x.T(root, isCn ? "cbc:CreditNoteTypeCode" : "cbc:InvoiceTypeCode");
            if (typeCode == "384")
                o.Add(W("SK-FS-17", "Type code 384 (corrected invoice): FR SR currently recommends a credit note plus a new invoice for corrections; 384 is planned for a later Peppol BIS release.",
                    new[] { "BT-3" }, "Issue a credit note (381) and a new invoice (380) instead."));
            var block = new Dictionary<string, object?>
            {
                ["applies"] = true, ["transposition"] = TRANSPOSITION, ["source"] = SOURCE,
                ["checks"] = "W System field checks from the FR SR transposition; not the official Slovak/Peppol schematron.",
                ["seller_dic"] = dic != "" ? dic : null, ["participant_id"] = dic != "" ? "0245:" + dic : null,
            };
            var number = x.T(root, "cbc:ID");
            if (epId != "" && epScheme != "" && typeCode != "" && number != "" && issue != "")
            {
                block["invoice_uuid"] = InvoiceUuid(epScheme, epId, typeCode, number, issue);
                block["invoice_uuid_input"] = string.Join(" ", epScheme, epId, typeCode, number, issue);
                block["invoice_uuid_note"] = "UUIDv5 over 'BT-34 schemeID, BT-34, BT-3, BT-1, BT-2' joined by single spaces, namespace 3fd00464-9f15-52ce-8cdc-6dd374d5ad42, per the BT-34 note in the "
                    + "transposition. The access points (C2/C3) compute the official value; the published example in the document is inconsistent, so treat this as a cross-check.";
            }
            return (o, block);
        }
    }
}
