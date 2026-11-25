using BuildingFex.Api.Incidents.Infrastructure.Triage;

namespace BuildingFex.Api.Tests.Incidents;

public class TriageParsingTests
{
    [Fact]
    public void Valid_reply_is_normalized_to_the_allowed_categories()
    {
        var suggestion = GeminiIncidentTriageAdvisor.Parse(
            "```json\n{\"category\": \"gasfiteria\", \"priority\": \"HIGH\", \"reason\": \"Fuga en varios pisos.\"}\n```");

        Assert.NotNull(suggestion);
        Assert.Equal("Gasfitería", suggestion!.Category);
        Assert.Equal("high", suggestion.Priority);
        Assert.Equal("Fuga en varios pisos.", suggestion.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("{\"category\": \"Astronomía\", \"priority\": \"urgentísima\"}")]
    public void Unusable_replies_are_rejected(string reply) =>
        Assert.Null(GeminiIncidentTriageAdvisor.Parse(reply));
}
