using Sim.Core.Simulation;
using Sim.Net;

namespace Sim.Tests;

public class AccountTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (WorldHost Host, AccountBook Book) Server()
    {
        var world = new GameWorld(42);
        world.LastActiveUtc = T0;
        var book = new AccountBook();
        return (new WorldHost(world, nowUtc: T0, accounts: book), book);
    }

    /// <summary>Says hello and runs the exchange to the end (the auth round trip takes a few updates).</summary>
    private static LoopbackPeer Join(WorldHost host, string name, string? password, DateTimeOffset? now = null)
    {
        var peer = new LoopbackPeer(host, name, password);
        peer.Client.Hello();
        for (int i = 0; i < 4; i++)
        {
            host.Update(0, now ?? T0);
            peer.Client.Poll();
        }
        return peer;
    }

    private static void Leave(WorldHost host, LoopbackPeer peer)
    {
        host.Disconnected(peer);
        host.Update(0, T0);
    }

    [Fact]
    public void TheFirstJoinClaimsTheNameAndLaterOnesMustProveIt()
    {
        var (host, book) = Server();
        var first = Join(host, "alice", "correct horse");
        Assert.True(first.Client.Connected, first.Client.Error);
        Assert.True(book.Has("alice"));
        Leave(host, first);

        var again = Join(host, "alice", "correct horse");
        Assert.True(again.Client.Connected, again.Client.Error);
        Leave(host, again);

        var impostor = Join(host, "alice", "battery staple");
        Assert.False(impostor.Client.Connected);
        Assert.Equal("Wrong password", impostor.Client.Error);
        Assert.Empty(host.Players);

        var noPassword = Join(host, "alice", null);
        Assert.False(noPassword.Client.Connected);
        Assert.Contains("password", noPassword.Client.Error);
    }

    [Fact]
    public void ThePasswordNeverGoesOverTheWire()
    {
        var (host, _) = Server();
        var first = Join(host, "bob", "hunter2-secret");
        Leave(host, first);
        var second = Join(host, "bob", "hunter2-secret");
        Assert.True(second.Client.Connected);
        Assert.All(first.Sent.Concat(second.Sent), line => Assert.DoesNotContain("hunter2-secret", line));
    }

    [Fact]
    public void TooManyWrongPasswordsLockTheNameForAWhile()
    {
        var (host, _) = Server();
        Leave(host, Join(host, "carol", "right"));
        for (int i = 0; i < AccountBook.MaxFailures; i++)
            Assert.Equal("Wrong password", Join(host, "carol", "wrong").Client.Error);

        var locked = Join(host, "carol", "right");
        Assert.False(locked.Client.Connected);
        Assert.Contains("Too many", locked.Client.Error);

        var later = Join(host, "carol", "right", T0 + AccountBook.LockoutTime + TimeSpan.FromSeconds(1));
        Assert.True(later.Client.Connected, later.Client.Error);
    }

    [Fact]
    public void AccountsAreSavedAndCanBeForgotten()
    {
        string path = Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}.json");
        try
        {
            var book = AccountBook.Load(path);
            var world = new GameWorld(42);
            world.LastActiveUtc = T0;
            var host = new WorldHost(world, nowUtc: T0, accounts: book);
            Leave(host, Join(host, "dave", "pw"));

            var reloaded = AccountBook.Load(path);
            Assert.True(reloaded.Has("dave"));
            Assert.DoesNotContain("pw\"", File.ReadAllText(path));
            Assert.True(reloaded.Forget("dave"));
            Assert.False(AccountBook.Load(path).Has("dave"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
