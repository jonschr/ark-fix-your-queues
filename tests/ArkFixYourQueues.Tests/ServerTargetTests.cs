using ArkFixYourQueues;

namespace ArkFixYourQueues.Tests;

public sealed class ServerTargetTests
{
    [Theory]
    [InlineData("1.2.3.4:7777")]
    [InlineData("open 1.2.3.4:7777")]
    public void ParsesDirectEndpoint(string input)
    {
        Assert.True(ServerTarget.TryParse(input, "My server", out var target, out _));
        Assert.Equal("open 1.2.3.4:7777", target!.JoinCommand);
        Assert.Equal("My server", target.DisplayName);
    }

    [Theory]
    [InlineData("example.com:7777")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4:0")]
    public void RejectsUnsupportedEndpoint(string input)
    {
        Assert.False(ServerTarget.TryParse(input, null, out _, out _));
    }
}
