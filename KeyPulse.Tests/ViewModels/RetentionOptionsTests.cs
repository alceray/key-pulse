using KeyPulse.ViewModels.Settings;

namespace KeyPulse.Tests.ViewModels;

public class RetentionOptionsTests
{
    [Fact]
    public void All_OrdersShortestToLongestWithForeverLast()
    {
        RetentionOptions.All.Select(o => o.Months).ShouldBe([6, 12, 24, 0]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 6)]
    [InlineData(12, 12)]
    [InlineData(24, 24)]
    public void FromMonths_KnownValue_ReturnsMatchingOption(int months, int expected) =>
        RetentionOptions.FromMonths(months).Months.ShouldBe(expected);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(48)]
    public void FromMonths_UnknownValue_FallsBackToForever(int months) =>
        RetentionOptions.FromMonths(months).Months.ShouldBe(0);
}
