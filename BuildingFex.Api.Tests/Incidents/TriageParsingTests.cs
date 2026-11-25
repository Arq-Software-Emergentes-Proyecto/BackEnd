using BuildingFex.Api.Incidents.Infrastructure.Triage;
using Xunit;

namespace BuildingFex.Api.Tests.Incidents;

public class TriageParsingTests
{
    [Fact]
    public void Valid_reply_is_normalized_to_the_allowed_categories()
    {
        var reply = """
            ```json
            {"category": "gasfiteria", "priority": "HIGH", "reason": "Fuga en varios pisos."}
            ```
            """;

        var result = GeminiIncidentTriageAdvisor.Parse(reply);

        Assert.NotNull(result);
        Assert.Equal("Gasfitería", result.Category);
        Assert.Equal("high", result.Priority);
        Assert.Equal("Fuga en varios pisos.", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("{\"category\": \"Astronomía\", \"priority\": \"urgentísima\"}")]
    public void Unusable_replies_are_rejected(string input)
    {
        Assert.Null(GeminiIncidentTriageAdvisor.Parse(input));
    }
}
