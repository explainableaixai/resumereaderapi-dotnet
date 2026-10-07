using AlphaQuantum.ResumeReaderApi;
using Xunit;

public class PayloadTests
{
    [Fact]
    public void PayloadCarriesOptions()
    {
        var c = new ResumeReaderClient("k");
        var p = c.BuildParsePayload("text", "cv", new ParseOptions(FieldNames: "fr", Anonymize: true));
        Assert.Equal(2, (int)p["schema_version"]!);
        Assert.True((bool)p["anonymize"]!);
        Assert.Equal("fr", (string)p["field_names"]!);
        Assert.Null(p["exclude_sensitive"]);
    }
}
