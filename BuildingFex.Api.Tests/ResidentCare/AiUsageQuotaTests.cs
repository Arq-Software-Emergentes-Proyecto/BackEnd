using BuildingFex.Api.ResidentCare.Application;
using Xunit;

namespace BuildingFex.Api.Tests.ResidentCare;

public class AiUsageQuotaTests
{
    [Fact]
    public void Period_and_day_use_lima_time()
    {
        // 2026-11-01 03:00 UTC es 2026-10-31 22:00 en Lima (UTC-5)
        var utc = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-10", AiUsageQuota.CurrentPeriod(utc));
        Assert.Equal("2026-10-31", AiUsageQuota.CurrentDay(utc));
    }

    [Fact]
    public void Snapshot_reports_remaining_queries_and_cap()
    {
        var under = new AiUsageSnapshot("2026-11", Used: 148, Cap: 150);
        Assert.Equal(2, under.Remaining);
        Assert.False(under.CapReached);

        var atCap = new AiUsageSnapshot("2026-11", Used: 150, Cap: 150);
        Assert.Equal(0, atCap.Remaining);
        Assert.True(atCap.CapReached);
    }
}
