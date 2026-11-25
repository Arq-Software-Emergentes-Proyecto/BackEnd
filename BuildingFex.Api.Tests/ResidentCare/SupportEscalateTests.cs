using BuildingFex.Api.ResidentCare.Domain;
using Xunit;

namespace BuildingFex.Api.Tests.ResidentCare;

public class SupportEscalateTests
{
    [Theory]
    [InlineData("Pásame con el administrador, por favor")]
    [InlineData("quiero hablar con administración")]
    [InlineData("Can I talk to building admin?")]
    public void Resident_requests_for_a_person_are_detected(string text)
    {
        Assert.True(SupportEscalate.ResidentWantsAdmin(text));
    }

    [Theory]
    [InlineData("¿Cuándo vence la cuota?")]
    [InlineData("")]
    public void Regular_questions_are_not_escalated(string text)
    {
        Assert.False(SupportEscalate.ResidentWantsAdmin(text));
    }

    [Fact]
    public void Escalate_marker_is_stripped()
    {
        var text = "Te derivo. [[ESCALATE_ADMIN]]";

        Assert.True(SupportEscalate.AssistantWantsEscalate(text));
        Assert.Equal("Te derivo.", SupportEscalate.StripEscalateMarker(text));
    }
}
