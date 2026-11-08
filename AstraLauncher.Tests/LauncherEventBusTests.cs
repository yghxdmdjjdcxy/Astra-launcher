using AstraLauncher.Plugins;

namespace AstraLauncher.Tests;

public sealed class LauncherEventBusTests
{
    private sealed record TestMessage(string Value);

    [Fact]
    public void Publish_DeliversToAllSubscribers_AndDisposeUnsubscribes()
    {
        var bus = new LauncherEventBus();
        var first = new List<string>();
        var second = new List<string>();
        IDisposable firstToken = bus.Subscribe<TestMessage>(message => first.Add(message.Value));
        bus.Subscribe<TestMessage>(message => second.Add(message.Value));

        bus.Publish(new TestMessage("hello"));

        Assert.Equal(["hello"], first);
        Assert.Equal(["hello"], second);

        firstToken.Dispose();
        bus.Publish(new TestMessage("again"));
        Assert.Equal(["hello"], first);
        Assert.Equal(["hello", "again"], second);
    }

    [Fact]
    public void Publish_KeepsMessageTypesSeparate()
    {
        var bus = new LauncherEventBus();
        string? received = null;
        bus.Subscribe<TestMessage>(message => received = message.Value);

        bus.Publish("not a TestMessage");

        Assert.Null(received);
    }

    [Fact]
    public void Publish_IsolatesFailingHandlers_AndReportsThem()
    {
        var failures = new List<Exception>();
        var bus = new LauncherEventBus(failures.Add);
        var received = new List<string>();
        bus.Subscribe<TestMessage>(_ => throw new InvalidOperationException("broken handler"));
        bus.Subscribe<TestMessage>(message => received.Add(message.Value));

        bus.Publish(new TestMessage("still delivered"));

        Assert.Single(failures);
        Assert.Equal(["still delivered"], received);
    }

    [Fact]
    public void Publish_WithNoSubscribers_DoesNothing()
    {
        var bus = new LauncherEventBus();
        bus.Publish(new TestMessage("nobody listens"));
    }
}
