using SourceFlow.Cloud.GCP.Configuration;
using SourceFlow.Cloud.GCP.Tests.TestHelpers;

namespace SourceFlow.Cloud.GCP.Tests.Unit;

[Trait("Category", TestCategories.Unit)]
public class GcpOptionsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var options = new GcpOptions();

        Assert.Equal(string.Empty, options.ProjectId);
        Assert.True(options.EnableCommandRouting);
        Assert.True(options.EnableEventRouting);
        Assert.True(options.EnableCommandListener);
        Assert.True(options.EnableEventListener);
        Assert.Equal(10, options.MaxMessagesPerPull);
        Assert.Equal(60, options.AckDeadlineSeconds);
        Assert.Equal("-sub", options.SubscriptionSuffix);
    }
}
