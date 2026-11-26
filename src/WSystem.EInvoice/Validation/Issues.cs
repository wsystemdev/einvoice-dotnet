using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSystem.EInvoice.Validation
{
    /// <summary>Turns schematron output into result entries and applies profile severity rules.</summary>
    internal static class Issues
    {
        public static readonly Dictionary<string, string> FIX_HINTS = new Dictionary<string, string>
        {
            ["BR-01"] = "Add the specification identifier (UBL cbc:CustomizationID / CII GuidelineSpecifiedDocumentContextParameter).",
            ["BR-02"] = "Add an invoice number.",
            ["BR-03"] = "Add the invoice issue date (YYYY-MM-DD).",
            ["BR-05"] = "Add the invoice currency code (e.g. EUR).",
            ["BR-06"] = "Add the seller's legal name.",
            ["BR-07"] = "Add the buyer's name.",
            ["BR-08"] = "Add the seller's postal address.",
            ["BR-09"] = "Add the seller's country code (ISO 3166-1 alpha-2).",
            ["BR-10"] = "Add the buyer's postal address.",
            ["BR-11"] = "Add the buyer's country code.",
            ["BR-16"] = "Add at least one invoice line.",
            ["BR-CO-10"] = "Sum of line net amounts must equal the document line total: recompute LineExtensionAmount.",
            ["BR-CO-13"] = "Total without VAT must be line total minus allowances plus charges.",
            ["BR-CO-15"] = "Total with VAT must be total without VAT plus total VAT.",
            ["BR-CO-16"] = "Amount due must be total with VAT minus prepaid amount plus rounding.",
            ["BR-CO-17"] = "VAT amount per category must be taxable amount x rate, rounded to 2 decimals.",
            ["BR-CO-26"] = "Give the seller a VAT ID, a legal registration ID or another identifier.",
            ["BR-S-08"] = "VAT breakdown for standard rate must match the sum of the S-rated lines.",
            ["BR-AE-02"] = "Reverse charge (AE) needs the seller's VAT ID and the buyer's VAT ID.",
            ["BR-DE-1"] = "XRechnung needs payment instructions (payment means).",
            ["BR-DE-2"] = "XRechnung needs a seller contact (name, phone and e-mail).",
            ["BR-DE-15"] = "XRechnung needs a Buyer reference (Leitweg-ID for German public buyers).",
            ["BR-DE-16"] = "XRechnung needs the seller VAT ID, tax number or tax representative.",
            ["PEPPOL-EN16931-R020"] = "Add the seller electronic address (EndpointID with schemeID).",
        };

        private static readonly (string Key, string Bt, string Who)[] COUNTRY_PARTY_BT =
        {
            ("AccountingSupplierParty", "BT-40", "seller"), ("SellerTradeParty", "BT-40", "seller"),
            ("AccountingCustomerParty", "BT-55", "buyer"), ("BuyerTradeParty", "BT-55", "buyer"),
            ("SellerTaxRepresentativeTradeParty", "BT-69", "seller tax representative"),
            ("TaxRepresentativeParty", "BT-69", "seller tax representative"),
            ("OriginCountry", "BT-159", "item country of origin"),
            ("ShipToTradeParty", "BT-80", "deliver-to"), ("Delivery", "BT-80", "deliver-to"),
        };

        public static readonly Dictionary<string, Dictionary<string, string>> XRECHNUNG_EXT_LEVELS = new Dictionary<string, Dictionary<string, string>>
        {
            ["cii"] = new Dictionary<string, string>
            {
                ["BR-CL-23"] = "warning", ["BR-CL-21"] = "information", ["BR-CL-11"] = "information", ["BR-CL-10"] = "information",
                ["BR-CL-24"] = "information", ["BR-CL-25"] = "information", ["BR-CL-26"] = "information",
                ["CII-SR-452"] = "error", ["CII-SR-453"] = "error", ["CII-SR-454"] = "error", ["CII-SR-465"] = "error", ["CII-SR-466"] = "error",
                ["CII-SR-475"] = "information", ["CII-SR-476"] = "information",
            },
            ["ubl"] = new Dictionary<string, string>
            {
                ["BR-CL-21"] = "information", ["BR-CL-23"] = "warning", ["BR-CL-24"] = "information", ["BR-CL-10"] = "information",
                ["BR-CL-11"] = "information", ["BR-CL-25"] = "information", ["BR-CL-26"] = "information", ["BR-CO-16"] = "information",
                ["UBL-CR-470"] = "information", ["UBL-CR-646"] = "information",
            },
        };

        private static readonly Regex BtRe = new Regex(@"\b(B[TG]-[0-9]+)\b", RegexOptions.CultureInvariant);

        public static ValidationIssue Entry(string ruleId, string flag, string ruleset, string text, string? location)
        {
            flag = (flag.Length == 0 ? "fatal" : flag).ToLowerInvariant();
            var bts = BtRe.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var e = new ValidationIssue
            {
                RuleId = ruleId, Severity = flag == "warning" || flag == "information" || flag == "info" ? "warning" : "error",
                Ruleset = ruleset, Message = Clean(text), Location = location, BusinessTerms = bts,
            };
            if (FIX_HINTS.TryGetValue(ruleId, out var fix)) e.Fix = fix;
            if (ruleId.StartsWith("BR-CL-", StringComparison.Ordinal))
            {
                var (clBts, clFix) = ClTerms(ruleId, location ?? "");
                if (clBts.Count > 0 && bts.Count == 0) e.BusinessTerms = clBts;
                if (clFix != null && e.Fix == null) e.Fix = clFix;
            }
            return e;
        }

        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);
        private static readonly Regex Prefix = new Regex(@"^\[[^\]]+\]\s*-?\s*", RegexOptions.CultureInvariant);

        public static string Clean(string text)
        {
            var t = Spaces.Replace(text, " ").Trim();
            return Prefix.Replace(t, "", 1);
        }

        public static (List<string>, string?) ClTerms(string rid, string loc)
        {
            loc = Regex.Replace(loc, @"\[[^\]]*\]", "");
            if (rid == "BR-CL-14")
            {
                foreach (var (key, bt, who) in COUNTRY_PARTY_BT)
                    if (loc.Contains(key))
                        return (new List<string> { bt }, $"The {who} country code ({bt}) must be an ISO 3166-1 alpha-2 code such as DE, FR or AT, "
                            + "not a country name like 'Deutschland' or 'Germany'. Map names to codes before writing the XML.");
                return (new List<string>(), "Country codes must be ISO 3166-1 alpha-2 (e.g. DE), not country names.");
            }
            if (rid == "BR-CL-03" || rid == "BR-CL-04" || rid == "BR-CL-05")
                return (new List<string> { loc.Contains("TaxCurrencyCode") ? "BT-6" : "BT-5" }, "Use an ISO 4217 currency code such as EUR, not a symbol or a name like 'Euro'.");
            switch (rid)
            {
                case "BR-CL-01": return (new List<string> { "BT-3" }, "Use a UNTDID 1001 invoice type code such as 380 (invoice) or 381 (credit note).");
                case "BR-CL-16": return (new List<string> { "BT-81" }, "Use a UNTDID 4461 payment means code such as 58 (SEPA credit transfer) or 30 (credit transfer).");
                case "BR-CL-17": case "BR-CL-18": return (new List<string>(), "Use a UNCL 5305 VAT category code: S, Z, E, AE, K, G, O, L or M.");
                case "BR-CL-23": return (new List<string> { "BT-130" }, "Use a UN/ECE Recommendation 20/21 unit code such as C62 (piece), HUR (hour) or DAY, not 'Stk' or 'pcs'.");
                default: return (new List<string>(), null);
            }
        }

        public static ValidationIssue ExtendedRelax(ValidationIssue i)
        {
            var rid = i.RuleId;
            if (i.Ruleset == "EN16931" && i.Severity == "error" && (rid.StartsWith("CII-SR-", StringComparison.Ordinal) || rid.StartsWith("CII-DT-", StringComparison.Ordinal)))
            {
                i = i.Clone();
                i.Severity = "warning";
                i.ProfileNote = "Allowed in Factur-X/ZUGFeRD EXTENDED; an error only for EN 16931 (COMFORT) and stricter profiles.";
            }
            return i;
        }

        public static ValidationIssue XrechnungExtLevel(ValidationIssue i, string syntax)
        {
            if (!XRECHNUNG_EXT_LEVELS[syntax].TryGetValue(i.RuleId, out var lvl) || (i.Ruleset != "EN16931" && i.Ruleset != "XRechnung")) return i;
            var sev = lvl == "error" ? "error" : "warning";
            if (sev == i.Severity) return i;
            i = i.Clone();
            i.Severity = sev;
            i.ProfileNote = sev == "warning"
                ? "Allowed in XRechnung Extension (KoSIT Extension scenario level: " + lvl + "); an error for standard XRechnung / EN 16931."
                : "An error in XRechnung Extension (KoSIT Extension scenario level: error).";
            return i;
        }
    }
}
