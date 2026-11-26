using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSystem.EInvoice.Validation
{
    /// <summary>
    /// Validation by the W System API or a self-hosted W System validator (official KoSIT/CEN rules, engine "remote").
    /// Opt-in only: the invoice file is sent to the URL you configure (default https://api.wsystem.dev) and leaves this
    /// machine. Nothing in this package uses it unless you construct it yourself; it is never a silent fallback.
    /// </summary>
    /// <example>
    /// var v = new HttpValidator(apiKey: Environment.GetEnvironmentVariable("WSYSTEM_API_KEY")); // keyless works within the free tier
    /// var result = v.Validate(File.ReadAllBytes("invoice.xml"));
    /// </example>
    public sealed class HttpValidator : IValidator
    {
        public const string DefaultBaseUrl = "https://api.wsystem.dev";
        private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(() => new HttpClient());
        private readonly string _baseUrl;
        private readonly string? _apiKey;
        private readonly HttpClient _http;
        private readonly TimeSpan _timeout;

        public HttpValidator(string baseUrl = DefaultBaseUrl, string? apiKey = null, HttpClient? httpClient = null, TimeSpan? timeout = null)
        {
            if (!Regex.IsMatch(baseUrl, "^https?://")) throw new ArgumentException("baseUrl must be an http(s) URL", nameof(baseUrl));
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _http = httpClient ?? Shared.Value;
            _timeout = timeout ?? TimeSpan.FromSeconds(60);
        }

        public ValidationResult Validate(byte[] data, string rules = "auto") => ValidateAsync(data, rules).GetAwaiter().GetResult();

        public async Task<ValidationResult> ValidateAsync(byte[] data, string rules = "auto", CancellationToken ct = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            rules = (rules == "" ? "auto" : rules).ToLowerInvariant();
            if (rules != "auto" && rules != "en16931" && rules != "xrechnung") throw new InputError("rules must be auto, en16931 or xrechnung (peppol is not available yet)");
            if (data.Length == 0) throw new InputError("empty input: pass the invoice XML or PDF bytes");
            var head = Support.Bytes.Latin1(data.Take(1024).ToArray()).TrimStart();
            var url = _baseUrl + "/einvoice/validate?rules=" + Uri.EscapeDataString(rules);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(data) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(head.StartsWith("%PDF-", StringComparison.Ordinal) ? "application/pdf" : "application/xml");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd("wsystem-einvoice-dotnet");
            if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add("X-API-Key", _apiKey);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException) { throw new InvalidOperationException($"could not reach {url}", e); }
            int status; string body;
            using (resp)
            {
                status = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            return Interpret(status, body, url, _baseUrl);
        }

        internal static ValidationResult Interpret(int status, string body, string url, string baseUrl)
        {
            JsonElement? json = null;
            try { using var d = JsonDocument.Parse(body); json = d.RootElement.Clone(); } catch (JsonException) { }
            bool isObj = json?.ValueKind == JsonValueKind.Object;
            string Field(params string[] names)
            {
                foreach (var n in names)
                    if (isObj && json!.Value.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null)
                        return v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString();
                return "";
            }
            if (status == 400 && isObj)
            {
                var m = Field("message", "detail", "error");
                throw new InputError(m != "" ? m : "the API rejected the input");
            }
            if (status != 200 || !isObj || !json!.Value.TryGetProperty("valid", out _))
            {
                var msg = isObj ? Field("message", "error") : "";
                throw new InvalidOperationException($"W System API answered HTTP {status}" + (msg != "" ? ": " + msg : "") + $" ({url})");
            }
            var r = JsonSerializer.Deserialize<ValidationResult>(body)!;
            r.Checked = r.Checked.ToDictionary(k => k.Key, k => Plain(k.Value));
            if (r.Slovakia != null) r.Slovakia = r.Slovakia.ToDictionary(k => k.Key, k => (object?)Plain(k.Value));
            if (r.Norway != null) r.Norway = r.Norway.ToDictionary(k => k.Key, k => (object?)Plain(k.Value));
            var official = r.Checked.TryGetValue("EN16931", out var en) && en is bool b && b;
            r.Engine = "remote"; r.Backend = "remote"; r.Conformance = official ? "official" : "pre-check";
            r.RemoteUrl = baseUrl;
            return r;
        }

        private static object Plain(object? v) => v is JsonElement e ? (Support.Json.FromElement(e) ?? "") : v ?? "";
    }
}
