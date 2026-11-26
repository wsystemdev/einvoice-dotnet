using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSystem.EInvoice
{
    /// <summary>
    /// Invoice input: the same fields as the W System API's POST /einvoice/create (snake_case in JSON). You can also
    /// pass that JSON (or a dictionary of the same shape) straight to <see cref="EInvoice.Create(string)"/>.
    /// </summary>
    public sealed class Invoice
    {
        /// <summary>xrechnung (UBL, default for DE public buyers), xrechnung-cii, en16931 (UBL), cii, peppol-bis3, zugferd / factur-x (PDF/A-3 + CII EN 16931), zugferd-xrechnung (PDF/A-3 + CII XRechnung)</summary>
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("invoice_number")] public string? InvoiceNumber { get; set; }
        /// <summary>UNTDID 1001: 380 invoice (default), 381 credit note, 384 corrected, 389 self-billed, 326 partial, 875/877 construction, 386 prepayment</summary>
        [JsonPropertyName("type_code")] public string? TypeCode { get; set; }
        [JsonPropertyName("issue_date")] public DateTime? IssueDate { get; set; }
        [JsonPropertyName("due_date")] public DateTime? DueDate { get; set; }
        [JsonPropertyName("delivery_date")] public DateTime? DeliveryDate { get; set; }
        [JsonPropertyName("tax_point_date")] public DateTime? TaxPointDate { get; set; }
        /// <summary>ISO 4217, e.g. EUR</summary>
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        /// <summary>BT-10; the Leitweg-ID for German public buyers (mandatory in XRechnung)</summary>
        [JsonPropertyName("buyer_reference")] public string? BuyerReference { get; set; }
        [JsonPropertyName("order_reference")] public string? OrderReference { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        /// <summary>BT-25: the invoice a credit note or correction refers to</summary>
        [JsonPropertyName("preceding_invoice")] public string? PrecedingInvoice { get; set; }
        [JsonPropertyName("preceding_invoice_date")] public DateTime? PrecedingInvoiceDate { get; set; }
        [JsonPropertyName("delivery_country")] public string? DeliveryCountry { get; set; }
        [JsonPropertyName("prepaid_amount")] public decimal? PrepaidAmount { get; set; }
        [JsonPropertyName("seller")] public Party? Seller { get; set; }
        [JsonPropertyName("buyer")] public Party? Buyer { get; set; }
        [JsonPropertyName("lines")] public List<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
        [JsonPropertyName("payment")] public Payment? Payment { get; set; }
        /// <summary>Document-level allowances (BG-20)</summary>
        [JsonPropertyName("allowances")] public List<AllowanceCharge>? Allowances { get; set; }
        /// <summary>Document-level charges (BG-21)</summary>
        [JsonPropertyName("charges")] public List<AllowanceCharge>? Charges { get; set; }
        /// <summary>Exemption reason text per VAT category, e.g. { ["E"] = "Steuerfrei nach § 4 Nr. 21 UStG" }</summary>
        [JsonPropertyName("vat_exemption_reason")] public Dictionary<string, string>? VatExemptionReason { get; set; }
        /// <summary>VATEX code per VAT category, e.g. { ["K"] = "VATEX-EU-IC" }</summary>
        [JsonPropertyName("vat_exemption_code")] public Dictionary<string, string>? VatExemptionCode { get; set; }

        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new DateConverter(), new DecimalConverter() },
        };

        /// <summary>The API JSON for this invoice.</summary>
        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        private sealed class DateConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => DateTime.ParseExact(r.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            public override void Write(Utf8JsonWriter w, DateTime v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        /// <summary>Writes decimals without trailing zeros (10.0m -> 10, 12.990m -> 12.99), as a JSON client would.</summary>
        private sealed class DecimalConverter : JsonConverter<decimal>
        {
            public override decimal Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.GetDecimal();
            public override void Write(Utf8JsonWriter w, decimal v, JsonSerializerOptions o)
            {
                var s = (v / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
                w.WriteRawValue(s, skipInputValidation: false);
            }
        }
    }

    /// <summary>Seller or buyer.</summary>
    public sealed class Party
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("trading_name")] public string? TradingName { get; set; }
        [JsonPropertyName("street")] public string? Street { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("postal_code")] public string? PostalCode { get; set; }
        /// <summary>ISO 3166-1 alpha-2, e.g. DE</summary>
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("vat_id")] public string? VatId { get; set; }
        /// <summary>National tax number (Steuernummer), BT-32</summary>
        [JsonPropertyName("tax_number")] public string? TaxNumber { get; set; }
        /// <summary>Legal registration id (BT-30 / BT-47)</summary>
        [JsonPropertyName("company_id")] public string? CompanyId { get; set; }
        /// <summary>ICD scheme of company_id, e.g. 0192 (Norway), 0088 (GLN)</summary>
        [JsonPropertyName("company_id_scheme")] public string? CompanyIdScheme { get; set; }
        /// <summary>BT-33, e.g. "AS" or "GmbH, Amtsgericht Berlin HRB 12345"</summary>
        [JsonPropertyName("legal_form")] public string? LegalForm { get; set; }
        [JsonPropertyName("contact_name")] public string? ContactName { get; set; }
        [JsonPropertyName("phone")] public string? Phone { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        /// <summary>Electronic address (BT-34 / BT-49); defaults to the e-mail with scheme EM</summary>
        [JsonPropertyName("endpoint_id")] public string? EndpointId { get; set; }
        [JsonPropertyName("endpoint_scheme")] public string? EndpointScheme { get; set; }
    }

    public sealed class InvoiceLine
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("item_description")] public string? ItemDescription { get; set; }
        [JsonPropertyName("seller_item_id")] public string? SellerItemId { get; set; }
        [JsonPropertyName("quantity")] public decimal? Quantity { get; set; }
        /// <summary>UN/ECE Rec. 20 code, e.g. C62 (piece, default), HUR (hour), DAY, KGM</summary>
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        /// <summary>Net unit price</summary>
        [JsonPropertyName("unit_price")] public decimal? UnitPrice { get; set; }
        /// <summary>VAT rate in percent, e.g. 19</summary>
        [JsonPropertyName("vat_rate")] public decimal? VatRate { get; set; }
        /// <summary>UNCL 5305: S (default), Z, E, AE, K, G, O, L, M</summary>
        [JsonPropertyName("vat_category")] public string? VatCategory { get; set; }
        [JsonPropertyName("allowances")] public List<AllowanceCharge>? Allowances { get; set; }
        [JsonPropertyName("charges")] public List<AllowanceCharge>? Charges { get; set; }
    }

    /// <summary>An allowance or charge (line level, or document level with VatRate / VatCategory).</summary>
    public sealed class AllowanceCharge
    {
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("percent")] public decimal? Percent { get; set; }
        [JsonPropertyName("base_amount")] public decimal? BaseAmount { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        /// <summary>UNCL 5189 (allowances) / UNCL 7161 (charges)</summary>
        [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
        [JsonPropertyName("vat_rate")] public decimal? VatRate { get; set; }
        [JsonPropertyName("vat_category")] public string? VatCategory { get; set; }
    }

    public sealed class Payment
    {
        /// <summary>UNTDID 4461, default 58 (SEPA credit transfer) when an IBAN is given</summary>
        [JsonPropertyName("means_code")] public string? MeansCode { get; set; }
        [JsonPropertyName("iban")] public string? Iban { get; set; }
        [JsonPropertyName("bic")] public string? Bic { get; set; }
        [JsonPropertyName("account_name")] public string? AccountName { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("terms")] public string? Terms { get; set; }
    }
}
