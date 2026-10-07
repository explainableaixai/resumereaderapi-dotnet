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

<!--expanded-->
## Using the client in ASP.NET and background services

.NET teams usually meet resumes in two places: an upload form and a batch job. The client suits both. It accepts an `HttpClient`, so the platform's handler pipeline, proxies and lifetime rules apply. It exposes async methods with cancellation tokens. It throws one exception type, and the status code is a property you can switch on.

Register the client once and inject it where needed:

```csharp
builder.Services.AddHttpClient("resume");
builder.Services.AddSingleton(sp =>
    new ResumeReaderClient(
        builder.Configuration["ResumeReader:Key"]!,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("resume")));
```

Keep the key in a secret store, not in a settings file that is checked into source control.

## A resilient upload endpoint

A good endpoint treats the parser as an unreliable neighbour. It should accept the upload quickly, hand the work to a background service and tell the user what is happening. A status page that polls for the result is friendlier than a request that hangs for thirty seconds.

1. Accept the file and validate the extension and size.
2. Save it to temporary storage and create a job record.
3. Return an identifier immediately.
4. A hosted service picks up the job, calls `ParseFileAsync` and stores the JSON.
5. The client polls the identifier or receives a notification.

Delete temporary files in a `finally` block. Resumes contain personal data, and a crashed worker should not leave them lying around.

## Mapping to your own types

Define records for the parts of the response you keep, and read them from the `JsonObject`:

```csharp
public sealed record CandidateRow(string? Name, string? Email, string? City, string? Seniority, double? Years, string[] Skills);

static CandidateRow Map(JsonObject resume)
{
    var contact = resume["contact"]!.AsObject();
    var career = resume["career"]!.AsObject();
    var skills = resume["skills"]!["technical"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray();
    return new CandidateRow(
        contact["full_name"]?.GetValue<string>(),
        contact["emails"]!.AsArray().FirstOrDefault()?.GetValue<string>(),
        contact["address"]?["city"]?.GetValue<string>(),
        career["seniority_level"]?.GetValue<string>(),
        career["total_experience_years"]?.GetValue<double>(),
        skills);
}
```

Every key is always present, so the null forgiving operators on arrays and objects are safe. Scalars may be `null`, which the nullable types express.

## Options by audience

The `ParseOptions` record lets you create views for different audiences. A recruiter sees everything. A hiring manager in a blind review sees an anonymized version. A data analyst sees a copy without sensitive fields. Produce each from the same source file when needed, rather than storing one version and trying to redact it later.

`Sections` limits the extraction to the parts you name, such as contact details or skills. `MaxPages` bounds the pages processed. `Language` gives a hint for non English files. `FieldNames` set to `fr` returns French keys, which suits teams that work with French schemas.

## Normalization in data pipelines

Candidate data from different sources never agrees with itself. Use the normalizers to bring it together. Pull distinct values from your table, normalize them in batches of up to a hundred, and join the canonical values back. The client splits longer lists automatically and sums the credits used.

```csharp
var distinct = await db.Candidates.Select(c => c.RawTitle).Distinct().ToListAsync();
var result = await rr.NormalizeTitlesAsync(distinct);

foreach (var item in result.Results)
{
    var input = item["input"]!.GetValue<string>();
    var canonical = item["normalized_title"]?.GetValue<string>();
    await db.TitleMap.AddAsync(new TitleMap(input, canonical, item["seniority_level"]?.GetValue<string>()));
}
await db.SaveChangesAsync();
```

A normalized title carries a seniority level that runs from intern through owner and a job function such as engineering or sales. Those two fields make dashboards much easier to build, because you can group by level and function instead of by the thousand ways people write a job title.

## Monitoring

Track the count of parses, the count of each error status and the credits remaining reported in the response. Alert when the remaining credits drop below a threshold you choose, and alert on a rising rate of 422, which often means a new kind of scanned input. Log each failure with the file name and the status, but not with the contents of the file.

## Reading list

The [brand suitability and audience fit page](https://www.cookielessaudiences.com/use-cases/brand-fit-analysis.php) from a sibling service shows how coded audience attributes can score how well a site matches a target profile, which is a helpful analogy for scoring candidates against a role. For readers who evaluate staffing businesses, the page on [an add-on radar for buy and build programmes](https://www.acquisitionuniverse.com/use-cases/add-on-acquisition-radar.php) describes how a standing map of eligible companies is kept current.

## Troubleshooting

**402 on a long document.** Documents over ten pages cost two credits, and the check happens before processing. Top up or trim the document.

**422 on a photo of a resume.** The image may be too dark or too small. Ask for a clearer photo or a PDF.

**Slow parses.** Large scanned files take longer. Raise the timeout on the `HttpClient` for batch work.

**Different results for the same file.** Check the options. `Anonymize` and `ExcludeSensitive` change the output on purpose.

MIT licensed. Questions to info@alpha-quantum.com.

<!--extra-->
## Capacity planning in numbers

Plan with real arithmetic. One address can send 30 requests per 60 seconds, so 1,800 an hour. A resume of ten pages or fewer costs one credit, so a plan with 5,000 credits parses about 5,000 ordinary resumes a month. Normalizing 1,000 distinct titles costs 100 credits. If you parse 3,000 resumes and normalize the distinct titles, skills and locations of those candidates, add about 15 to 25 percent for the normalizers. Write those numbers into the design document, and check them against the credit fields in real responses during the first week.

Remember that normalized values are derived, not stored by the service. Save them next to the originals in your own tables, with the date and the method, and you can always explain how a canonical title came about. When a fallback result looks wrong, correct it in your own mapping table and let your correction win on later runs.

<!--further-->
## Further reading and practical notes

The [.NET documentation](https://learn.microsoft.com/en-us/dotnet/) explains `HttpClient`, hosted services, dependency injection and `System.Text.Json`. For anyone designing screening on top of parsed data, the [EEOC site](https://www.eeoc.gov/) publishes guidance on fair employment practices in the United States.

Three practical notes apply to a production deployment. First, separate the parse from the decision. The parser extracts facts. Whatever ranks or filters candidates should be a separate component with its own tests and its own documentation, so that a reviewer can examine the rule without reading the extraction code. Second, keep an audit trail. For each candidate, store when the file arrived, which options were used, which version of your mapping code ran and what the status was. When someone asks why a candidate was or was not shortlisted, the trail answers in minutes. Third, give candidates a path to correct their record. Parsing is accurate, not perfect, and a person should be able to fix a wrong date or a missing skill. Let their correction override the parsed value and keep both.

Finally, plan capacity honestly. At 30 requests per 60 seconds per IP, a single address can parse about 1,800 resumes an hour at best. If a campaign brings a spike of ten thousand applications, queue them and process over the following hours. Tell candidates their profile will be ready shortly, and make sure it is.

## FAQ

**Are documents saved?** They are processed in memory for the request.

**Large PDFs?** Up to 20 pages and 10 MB.

**Does it need a `HttpClient`?** Optional; pass one to share a handler.

MIT license. info@alpha-quantum.com
