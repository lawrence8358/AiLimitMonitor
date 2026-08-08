using AiLimitMonitor.Core.Rendering;

namespace AiLimitMonitor.Core.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(3, 14, "3h 14m")]
    [InlineData(7, 4, "7h 04m")]
    [InlineData(23, 59, "23h 59m")]  // under a day -> no day unit
    [InlineData(24, 0, "1d")]        // exact day -> no zero hours
    [InlineData(26, 30, "1d 2h")]    // day scale -> minutes dropped
    [InlineData(111, 57, "4d 15h")]
    public void Duration_scales_units_up_to_days(int hours, int minutes, string expected) =>
        Assert.Equal(expected, TimeFormat.Duration(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public void Duration_formats_minutes_only() =>
        Assert.Equal("45m", TimeFormat.Duration(TimeSpan.FromMinutes(45)));

    [Fact]
    public void Duration_formats_less_than_a_minute() =>
        Assert.Equal("<1m", TimeFormat.Duration(TimeSpan.FromSeconds(30)));

    [Fact]
    public void Duration_formats_expired_as_now() =>
        Assert.Equal("now", TimeFormat.Duration(TimeSpan.FromMinutes(-5)));

    [Theory]
    [InlineData(30, "30m")]
    [InlineData(200, "3h")]
    [InlineData(1800, "1d")] // 30h -> day unit once over 24h
    [InlineData(6720, "4d")] // 112h
    public void CompactDuration_scales_units(int minutes, string expected) =>
        Assert.Equal(expected, TimeFormat.CompactDuration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void ResetStamp_uses_taiwan_date_format_in_given_time_zone()
    {
        var utcPlus8 = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(8), "t", "t");
        var resetsAt = new DateTimeOffset(2026, 8, 8, 16, 10, 0, TimeSpan.Zero);

        Assert.Equal("2026/08/09 00:10 UTC+8", TimeFormat.ResetStamp(resetsAt, utcPlus8));
    }

    [Theory]
    [InlineData(8, 0, "UTC+8")]
    [InlineData(-5, 0, "UTC-5")]
    [InlineData(5, 30, "UTC+5:30")]
    [InlineData(0, 0, "UTC")]
    public void OffsetLabel_formats_offsets(int hours, int minutes, string expected)
    {
        var offset = new TimeSpan(Math.Abs(hours), minutes, 0);
        if (hours < 0)
            offset = offset.Negate();
        Assert.Equal(expected, TimeFormat.OffsetLabel(offset));
    }
}
