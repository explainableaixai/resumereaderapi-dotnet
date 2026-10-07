using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AlphaQuantum.ResumeReaderApi;

/// <summary>The HTTP status is always 200, so errors come from the status field of the JSON body.</summary>
public sealed class ResumeReaderException : Exception
{
    public int Status { get; }
    public JsonNode? Body { get; }

    public ResumeReaderException(int status, string message, JsonNode? body = null) : base($"[{status}] {message}")
    {
        Status = status;
        Body = body;
    }
}

/// <summary>Optional parse parameters.</summary>
public sealed record ParseOptions(
    string? FieldNames = null,
    bool ExcludeSensitive = false,
    bool Anonymize = false,
    int? MaxPages = null,
    string[]? Sections = null,
    string? Language = null);

public sealed record Normalized(List<JsonNode> Results, double CreditsUsed, double? RemainingCredits);

public sealed class ResumeReaderClient : IDisposable
{
    private const string Base = "https://www.resumereaderapi.com/api/";
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private static readonly Dictionary<int, string> StatusText = new()
    {
        [400] = "Missing or invalid input",
        [401] = "Invalid API key",
        [402] = "Insufficient credits, nothing was billed",
        [413] = "Document beyond 20 pages, about 30,000 tokens or 10 MB, nothing was billed",
        [422] = "File could not be read as a resume",
        [429] = "Rate limit exceeded, 30 requests per 60 seconds per IP",
    };

    public ResumeReaderClient(string apiKey, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API key is required", nameof(apiKey));
        _apiKey = apiKey;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("resumereaderapi-dotnet/1.0.0 (+https://www.resumereaderapi.com)");
    }

    private async Task<JsonObject> PostAsync(string endpoint, JsonObject payload, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(Base + endpoint, payload, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(text) as JsonObject
            ?? throw new ResumeReaderException((int)res.StatusCode, "Response was not a JSON object");
        var status = node["status"]?.GetValue<int>() ?? 200;
        if (status != 200)
            throw new ResumeReaderException(status, StatusText.GetValueOrDefault(status, "API error"), node);
        return node;
    }

    internal JsonObject BuildParsePayload(string key, JsonNode value, ParseOptions o, string? filename = null)
    {
        var p = new JsonObject { ["api_key"] = _apiKey, ["schema_version"] = 2, [key] = value };
        if (filename is not null) p["filename"] = filename;
        if (o.FieldNames is not null) p["field_names"] = o.FieldNames;
        if (o.ExcludeSensitive) p["exclude_sensitive"] = true;
        if (o.Anonymize) p["anonymize"] = true;
        if (o.MaxPages is int pages) p["max_pages"] = pages;
        if (o.Sections is { Length: > 0 }) p["sections"] = new JsonArray(o.Sections.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        if (o.Language is not null) p["language"] = o.Language;
        return p;
    }

    public Task<JsonObject> ParseTextAsync(string text, ParseOptions? options = null, CancellationToken ct = default) =>
        PostAsync("parse.php", BuildParsePayload("text", text, options ?? new()), ct);

    public async Task<JsonObject> ParseFileAsync(string path, ParseOptions? options = null, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return await PostAsync("parse.php",
            BuildParsePayload("file_base64", Convert.ToBase64String(bytes), options ?? new(), Path.GetFileName(path)), ct).ConfigureAwait(false);
    }

    public Task<JsonObject> ParseUrlAsync(string fileUrl, ParseOptions? options = null, CancellationToken ct = default) =>
        PostAsync("parse.php", BuildParsePayload("file_url", fileUrl, options ?? new()), ct);

    private async Task<Normalized> NormalizeAsync(string endpoint, string key, IEnumerable<string> items, CancellationToken ct)
    {
        var list = items.ToList();
        var results = new List<JsonNode>();
        double used = 0;
        double? remaining = null;
        for (var i = 0; i < list.Count; i += 100)
        {
            var chunk = list.Skip(i).Take(100).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray();
            var body = await PostAsync(endpoint, new JsonObject { ["api_key"] = _apiKey, [key] = new JsonArray(chunk) }, ct).ConfigureAwait(false);
            if (body["results"] is JsonArray arr) results.AddRange(arr.Where(n => n is not null).Select(n => n!.DeepClone()));
            used += body["credits_used"]?.GetValue<double>() ?? 0;
            remaining = body["remaining_credits"]?.GetValue<double>() ?? remaining;
        }
        return new Normalized(results, used, remaining);
    }

    public Task<Normalized> NormalizeTitlesAsync(IEnumerable<string> titles, CancellationToken ct = default) =>
        NormalizeAsync("normalize_title.php", "titles", titles, ct);

    public Task<Normalized> NormalizeSkillsAsync(IEnumerable<string> skills, CancellationToken ct = default) =>
        NormalizeAsync("normalize_skills.php", "skills", skills, ct);

    public Task<Normalized> NormalizeLocationsAsync(IEnumerable<string> locations, CancellationToken ct = default) =>
        NormalizeAsync("normalize_locations.php", "locations", locations, ct);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
