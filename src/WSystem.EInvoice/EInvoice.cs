using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WSystem.EInvoice.Generator;
using WSystem.EInvoice.Model;
using WSystem.EInvoice.Pdf;
using WSystem.EInvoice.Support;
using WSystem.EInvoice.Validation;

namespace WSystem.EInvoice
{
    /// <summary>Result of <see cref="EInvoice.Create(Invoice)"/>: same fields as the W System API's POST /einvoice/create.</summary>
    public sealed class CreateResult
    {
        /// <summary>null when no validator ran</summary>
        [JsonPropertyName("valid")] public bool? Valid { get; set; }
        [JsonPropertyName("format")] public string Format { get; set; } = "";
        /// <summary>"UBL 2.1" or "UN/CEFACT CII D16B"</summary>
        [JsonPropertyName("syntax")] public string Syntax { get; set; } = "";
        /// <summary>currency, line_total, vat_total, total_with_vat, payable (and allowance/charge totals when present)</summary>
        [JsonPropertyName("totals")] public Dictionary<string, string> Totals { get; set; } = new Dictionary<string, string>();
        [JsonPropertyName("xml")] public string Xml { get; set; } = "";
        [JsonPropertyName("errors")] public List<ValidationIssue> Errors { get; set; } = new List<ValidationIssue>();
        [JsonPropertyName("warnings")] public List<ValidationIssue> Warnings { get; set; } = new List<ValidationIssue>();
        [JsonPropertyName("checked")] public Dictionary<string, object>? Checked { get; set; }
        [JsonPropertyName("validator")] public string Validator { get; set; } = "none";
        [JsonPropertyName("engine")] public string Engine { get; set; } = "none";
        [JsonPropertyName("conformance")] public string? Conformance { get; set; }
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new List<string>();
        [JsonPropertyName("disclaimer")] public string Disclaimer { get; set; } = Codes.DISCLAIMER;
        // PDF formats (zugferd, factur-x, zugferd-xrechnung)
        [JsonPropertyName("embedded_file")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EmbeddedFile { get; set; }
        [JsonPropertyName("zugferd_profile")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ZugferdProfile { get; set; }
        /// <summary>The PDF/A-3b hybrid (base64 in JSON); null for XML formats, or when the embedded XML has errors.</summary>
        [JsonPropertyName("pdf")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte[]? Pdf { get; set; }
        [JsonPropertyName("pdf_filename")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PdfFilename { get; set; }
        [JsonPropertyName("pdf_info")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PdfInfo? PdfInfo { get; set; }
        [JsonPropertyName("pdf_info_replaced_characters")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? PdfInfoReplacedCharacters { get; set; }

        /// <summary>Writes the XML (or, for PDF formats, the PDF when one was built) to <paramref name="path"/>.</summary>
        public void Save(string path)
        {
            if (Pdf != null) File.WriteAllBytes(path, Pdf);
            else File.WriteAllBytes(path, Bytes.Utf8.GetBytes(Xml));
        }

        public string ToJson(bool indented = false) => System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = indented });
    }

    public sealed class PdfInfo
    {
        [JsonPropertyName("standard")] public string Standard { get; set; } = "PDF/A-3b";
        [JsonPropertyName("hybrid")] public string Hybrid { get; set; } = "";
        [JsonPropertyName("af_relationship")] public string AfRelationship { get; set; } = "Alternative";
        [JsonPropertyName("bytes")] public int Bytes { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    public sealed class EInvoiceOptions
    {
        /// <summary>Validator used by Create and Validate. Default: the local <see cref="Validation.Validator"/> (dotnet-core pre-check). Set <see cref="NoValidation"/> to skip.</summary>
        public IValidator? Validator { get; set; }
        /// <summary>Skip validation in Create ("valid" is then null and a PDF is always built).</summary>
        public bool NoValidation { get; set; }
        /// <summary>Time written into the PDF (Info, XMP, embedded file dates); default now. Fix it for reproducible files.</summary>
        public DateTimeOffset? PdfTime { get; set; }
    }

    /// <summary>
    /// Entry point. Create() builds the invoice locally (same XML as the W System API's POST /einvoice/create) and checks
    /// it before returning; for the PDF formats a PDF/A-3b hybrid is built only when the XML is valid. Validate() checks
    /// UBL / CII XML or a ZUGFeRD / Factur-X / XRechnung PDF.
    /// </summary>
    public sealed class EInvoice
    {
        public const string Version = "0.9.0-beta.2";

        private readonly IValidator? _validator;
        private readonly DateTimeOffset? _pdfTime;

        public EInvoice() : this(new EInvoiceOptions()) { }
        public EInvoice(IValidator validator) : this(new EInvoiceOptions { Validator = validator }) { }
        public EInvoice(EInvoiceOptions options)
        {
            _validator = options.NoValidation ? null : options.Validator ?? new Validator();
            _pdfTime = options.PdfTime;
        }

        /// <summary>Creates the e-invoice: XML (and PDF for ZUGFeRD / Factur-X), validated unless NoValidation is set.</summary>
        /// <exception cref="InputError">The invoice is not valid JSON, not a JSON object, or has missing or invalid fields
        /// (the same 400 errors as the API).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="invoice"/> is null.</exception>
        public CreateResult Create(Invoice invoice) => CreateFromValue(Json.Decode((invoice ?? throw new ArgumentNullException(nameof(invoice))).ToJson()));
        /// <summary>Creates the e-invoice from JSON.</summary>
        /// <param name="json">the invoice JSON (same shape as the API's create request)</param>
        /// <exception cref="InputError">The invoice is not valid JSON, not a JSON object, or has missing or invalid fields
        /// (the same 400 errors as the API).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public CreateResult Create(string json) => CreateFromValue(Json.Decode(json));
        /// <summary>Creates the e-invoice from a dictionary.</summary>
        /// <param name="invoice">a dictionary with the invoice JSON shape (nested dictionaries / lists, numbers, strings)</param>
        /// <exception cref="InputError">The invoice is not valid JSON, not a JSON object, or has missing or invalid fields
        /// (the same 400 errors as the API).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="invoice"/> is null.</exception>
        public CreateResult Create(IDictionary<string, object?> invoice) => CreateFromValue(Json.Normalize(invoice ?? throw new ArgumentNullException(nameof(invoice))));

        /// <summary>Validates an e-invoice (UBL or CII XML, or a ZUGFeRD / Factur-X PDF). Rule failures are returned in the
        /// result (<c>Valid == false</c>); input that is not an e-invoice at all throws instead.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="InputError">The input is empty, too large, not well-formed XML, not a UBL / CII invoice, or a PDF
        /// without a readable embedded invoice XML (including PDFs whose decompressed data exceeds 75 MB).</exception>
        /// <exception cref="InvalidOperationException">NoValidation is set.</exception>
        public ValidationResult Validate(byte[] data, string rules = "auto")
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (_validator == null) throw new InvalidOperationException("no validator configured (NoValidation is set)");
            return _validator.Validate(data, rules);
        }

        /// <summary>Validates an e-invoice file. See <see cref="Validate(byte[], string)"/>.</summary>
        /// <exception cref="InputError">See <see cref="Validate(byte[], string)"/>.</exception>
        public ValidationResult Validate(string path, string rules = "auto") => Validate(File.ReadAllBytes(path ?? throw new ArgumentNullException(nameof(path))), rules);

        /// <summary>Only the XML (UBL or CII) for an invoice, without validation or PDF.</summary>
        /// <exception cref="InputError">The invoice is not a JSON object or has missing or invalid fields.</exception>
        public static string Xml(Invoice invoice) => XmlOf(Json.Decode((invoice ?? throw new ArgumentNullException(nameof(invoice))).ToJson()));
        /// <exception cref="InputError">The text is not valid JSON, not a JSON object, or has missing or invalid fields.</exception>
        public static string Xml(string json) => XmlOf(Json.Decode(json));
        private static string XmlOf(object? invoice) =>
            invoice is Dictionary<string, object?> ? XmlBuilder.Build(invoice).Xml : throw new InputError("the invoice must be a JSON object");

        private static readonly Regex Unsafe = new Regex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

        private CreateResult CreateFromValue(object? invoice)
        {
            if (!(invoice is Dictionary<string, object?>)) throw new InputError("the invoice must be a JSON object");
            var b = XmlBuilder.Build(invoice);
            var notes = new List<string>();
            foreach (var ln in Py.AsList(Py.G(invoice, "lines")))
            {
                if (Py.IsDict(ln) && Codes.RETIRED_UNITS.ContainsKey(Py.Upper(Py.Trim(Py.Str(Py.T(Py.G(ln, "unit")) ? Py.G(ln, "unit") : "")))))
                {
                    notes.Add("Unit code PK (pack) was retired from UN/ECE Recommendation 20 and is rejected by BR-CL-23; written as XPK (package).");
                    break;
                }
            }
            if (b.Format == "peppol-bis3")
                notes.Add("Built to the Peppol BIS Billing 3.0 structure and checked against EN 16931 only; Peppol-specific rules are not run.");
            var v = _validator?.Validate(Bytes.Utf8.GetBytes(b.Xml), b.XRechnung ? "xrechnung" : "en16931");
            var engine = v == null ? "none" : (v.Engine != "" ? v.Engine : v.Backend);
            var r = new CreateResult
            {
                Valid = v?.Valid, Format = b.Format, Syntax = b.Syntax, Totals = b.Totals, Xml = b.Xml,
                Errors = v?.Errors ?? new List<ValidationIssue>(), Warnings = v?.Warnings ?? new List<ValidationIssue>(), Checked = v?.Checked,
                Validator = engine, Engine = engine, Conformance = v?.Conformance, Notes = notes,
                Disclaimer = v != null ? v.Disclaimer : Codes.NO_VALIDATION_DISCLAIMER,
            };
            if (b.Pdf != null)
            {
                r.EmbeddedFile = b.Pdf.FileName;
                r.ZugferdProfile = b.Pdf.Level == "EN 16931" ? "EN 16931 (COMFORT)" : "XRECHNUNG";
                if (v != null && !v.Valid)
                {
                    r.Pdf = null;
                    r.Notes.Add("No PDF was built because the embedded XML has errors: fix them and call again.");
                }
                else
                {
                    var (pdf, replaced) = HybridPdf.Build(invoice, b.Model, b.Xml, b.Format, b.Pdf.FileName!, b.Pdf.Level!, _pdfTime);
                    var safe = Unsafe.Replace(Py.Str(Py.G(invoice, "invoice_number")), "_");
                    safe = safe.Length > 80 ? safe.Substring(0, 80) : safe;
                    if (safe == "") safe = "invoice";
                    r.Pdf = pdf;
                    if (replaced)
                    {
                        r.Notes.Add("Some characters are outside the PDF fonts (Windows-1252) and were simplified or shown as \"?\" in the visual PDF only; the embedded XML keeps them unchanged.");
                        r.PdfInfoReplacedCharacters = true;
                    }
                    r.PdfFilename = safe + ".pdf";
                    string sha;
                    using (var h = SHA256.Create()) sha = string.Concat(h.ComputeHash(pdf).Select(x => x.ToString("x2")));
                    r.PdfInfo = new PdfInfo
                    {
                        Hybrid = b.Pdf.Level == "EN 16931" ? "ZUGFeRD 2.3 / Factur-X 1.07 EN 16931" : "ZUGFeRD 2.3 XRECHNUNG",
                        Bytes = pdf.Length, Sha256 = sha,
                    };
                }
            }
            if (b.XRechnung && b.Syntax != "UBL 2.1")
                r.Notes.Add("XRechnung 3.0 in CII syntax: checked against EN 16931 and the KoSIT XRechnung 3.0.2 rules.");
            return r;
        }
    }
}
