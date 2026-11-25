using BuildingFex.Api.ResidentCare.Application;

namespace BuildingFex.Api.Tests.ResidentCare;

public class AiUsageQuotaTests
{
    [Fact]
    public void Period_and_day_use_lima_time()
    {
        // 2026-11-01 03:00 UTC = 2026-10-31 22:00 en Lima (UTC-5).
        var now = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-10", AiUsageQuota.CurrentPeriod(now));
        Assert.Equal("2026-10-31", AiUsageQuota.CurrentDay(now));
    }

    [Fact]
    public void Snapshot_reports_remaining_queries_and_cap()
    {
        var snapshot = new AiUsageSnapshot("2026-11", Used: 148, Cap: 150);

        Assert.Equal(2, snapshot.Remaining);
        Assert.False(snapshot.CapReached);
        Assert.True(new AiUsageSnapshot("2026-11", 150, 150).CapReached);
    }
}
