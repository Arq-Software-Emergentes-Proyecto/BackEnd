using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;
using Xunit;

namespace BuildingFex.Api.Tests.Incidents;

public class EscalationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static Incident CreateIncident(int hoursAgo) =>
        Incident.Create(
            "incident-1",
            1,
            "Fuga de agua",
            reportedAt: Now - TimeSpan.FromHours(hoursAgo),
            title: "Fuga");

    [Fact]
    public void Open_and_unassigned_after_48_hours_is_escalated()
    {
        var overdue = CreateIncident(49);
        var withinLimit = CreateIncident(47);

        Assert.True(EscalationPolicy.MustEscalate(overdue, Now));
        Assert.False(EscalationPolicy.MustEscalate(withinLimit, Now));
    }

    [Fact]
    public void An_incident_is_escalated_only_once()
    {
        var incident = CreateIncident(60);

        Assert.True(incident.Escalate(Now));
        Assert.False(incident.Escalate(Now.AddHours(1)));
        Assert.True(incident.IsEscalationActive);
    }

    [Fact]
    public void Assigned_incidents_are_not_escalated()
    {
        var incident = CreateIncident(50);
        incident.Assign("provider-1", "Gasfitería Lima", IncidentActor.System);

        Assert.False(EscalationPolicy.MustEscalate(incident, Now));
    }
}
