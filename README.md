# AlphaQuantum.ResumeReaderApi for .NET

Read resumes from C#. Send a file, text or a link and get back a candidate with 114 fields as a `JsonObject`. The same client cleans up job titles, skills and locations.

Targets .NET 8. No third-party packages.

```bash
dotnet add package AlphaQuantum.ResumeReaderApi
```

## Parse a resume

```csharp
using AlphaQuantum.ResumeReaderApi;

using var rr = new ResumeReaderClient(Environment.GetEnvironmentVariable("RESUME_KEY")!);
var result = await rr.ParseFileAsync("candidate.pdf");

var contact = result["resume"]!["contact"]!;
Console.WriteLine(contact["full_name"]);
Console.WriteLine(result["remaining_credits"]);
```

## Options record

```csharp
var opts = new ParseOptions(
    FieldNames: "fr",
    ExcludeSensitive: true,
    Anonymize: true,
    MaxPages: 5,
    Sections: new[] { "contact", "skills" },
    Language: "de");

await rr.ParseTextAsync(cvText, opts);
```

Everything is optional. A bare `ParseOptions()` asks for the full English schema.

## All calls

| Method | Notes |
|---|---|
| `ParseTextAsync(text, options?)` | text input |
| `ParseFileAsync(path, options?)` | reads and base64 encodes the file |
| `ParseUrlAsync(url, options?)` | service downloads the link |
| `NormalizeTitlesAsync(items)` | returns `Normalized` with `Results`, `CreditsUsed`, `RemainingCredits` |
| `NormalizeSkillsAsync(items)` | same |
| `NormalizeLocationsAsync(items)` | same |

## ASP.NET minimal API upload

```csharp
app.MapPost("/cv", async (IFormFile cv, ResumeReaderClient rr) =>
{
    var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Path.GetExtension(cv.FileName));
    await using (var fs = File.Create(tmp)) await cv.CopyToAsync(fs);
    try { return Results.Json((await rr.ParseFileAsync(tmp))["resume"]); }
    catch (ResumeReaderException ex) { return Results.Problem(ex.Message, statusCode: 502); }
    finally { File.Delete(tmp); }
}).DisableAntiforgery();
```

## Job title normalization for search

Recruiters search "software engineer" and miss "SWE II". Normalize titles on ingest and index the canonical value next to the original.

```csharp
var titles = await rr.NormalizeTitlesAsync(new[] { "Sr. SWE II", "Growth Ninja" });
foreach (var t in titles.Results)
    Console.WriteLine($"{t["input"]} -> {t["normalized_title"]} ({t["seniority_level"]})");
```

The canonical lists and seniority levels are shown with real inputs on the [job title examples](https://www.resumereaderapi.com/normalization/job-titles-examples.php) page.

## Errors

`ResumeReaderException.Status` is 400, 401, 402, 413, 422 or 429. A 402 or 413 bills nothing. Throttle yourself to 30 calls a minute per IP.

## FAQ

**Are documents saved?** They are processed in memory for the request.

**Large PDFs?** Up to 20 pages and 10 MB.

**Does it need a `HttpClient`?** Optional; pass one to share a handler.

MIT license. info@alpha-quantum.com
