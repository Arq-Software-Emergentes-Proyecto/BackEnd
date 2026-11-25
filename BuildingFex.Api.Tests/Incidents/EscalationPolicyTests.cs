using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Tests.Incidents;

public class EscalationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static Incident Reported(double hoursAgo) =>
        Incident.Create("incident-1", 1, "Fuga de agua", reportedAt: Now.AddHours(-hoursAgo), title: "Fuga");

    [Fact]
    public void Open_and_unassigned_after_48_hours_is_escalated()
    {
        Assert.True(EscalationPolicy.MustEscalate(Reported(49), Now));
        Assert.False(EscalationPolicy.MustEscalate(Reported(47), Now));
    }

    [Fact]
    public void An_incident_is_escalated_only_once()
    {
        var incident = Reported(60);

        Assert.True(incident.Escalate(Now));
        Assert.False(incident.Escalate(Now.AddHours(1)));
        Assert.True(incident.IsEscalationActive);
    }

    [Fact]
    public void Assigned_incidents_are_not_escalated()
    {
        var incident = Reported(60);
        incident.Assign("provider-1", "Gasfitería Lima", IncidentActor.System);

        Assert.False(EscalationPolicy.MustEscalate(incident, Now));
    }
}
