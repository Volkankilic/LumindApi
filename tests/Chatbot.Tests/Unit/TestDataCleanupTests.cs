using Chatbot.Tests.TestSupport;

namespace Chatbot.Tests.Unit;

public class TestDataCleanupTests
{
    [Theory]
    [InlineData("itest-abc", true)]
    [InlineData("itest-" + "0123456789abcdef", true)]
    [InlineData("seed-1", false)]
    [InlineData("seed-itest-1", false)]
    [InlineData("ITEST-abc", false)]
    [InlineData("itest-", false)]
    [InlineData("itest", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyLabelsWithTheTestPrefixCountAsTestParticipants(string? label, bool expected) =>
        Assert.Equal(expected, TestDataCleanup.IsTestLabel(label));

    [Fact]
    public void NewLabels_AreTestLabels_AndUnique()
    {
        var a = TestDataCleanup.NewLabel();
        var b = TestDataCleanup.NewLabel();
        Assert.True(TestDataCleanup.IsTestLabel(a));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheGuard_AcceptsOnlyTestParticipants() =>
        TestDataCleanup.EnsureAllAreTestParticipants([(Guid.NewGuid(), "itest-1"), (Guid.NewGuid(), "itest-2")]);

    [Theory]
    [InlineData("seed-2")]
    [InlineData(null)]
    [InlineData("demo")]
    public void TheGuard_RefusesTheWholeBatch_WhenOneParticipantIsNotATestParticipant(string? foreignLabel)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestDataCleanup.EnsureAllAreTestParticipants([(Guid.NewGuid(), "itest-1"), (Guid.NewGuid(), foreignLabel)]));
        Assert.Contains("itest-", ex.Message, StringComparison.Ordinal);
    }
}
