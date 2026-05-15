using System.Net;
using System.Net.Sockets;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

public sealed class TcpPreviewPortAllocatorTests
{
    [Fact]
    public void AllocateSingle_Skips_Listening_Preferred_Port()
    {
        using var listener = StartListener();
        var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sut = new TcpPreviewPortAllocator();

        var allocated = sut.AllocateSingle(occupiedPort, Array.Empty<int>());

        Assert.NotEqual(occupiedPort, allocated);
        Assert.True(allocated > occupiedPort);
    }

    [Fact]
    public void AllocateSingle_Allows_Reusable_Listening_Port()
    {
        using var listener = StartListener();
        var reusablePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sut = new TcpPreviewPortAllocator();

        var allocated = sut.AllocateSingle(reusablePort, [reusablePort]);

        Assert.Equal(reusablePort, allocated);
    }

    [Fact]
    public void AllocateComparison_Skips_Pair_When_After_Port_Is_Listening()
    {
        using var listener = StartListener();
        var occupiedAfterPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sut = new TcpPreviewPortAllocator();

        var pair = sut.AllocateComparison(occupiedAfterPort, Array.Empty<int>());

        Assert.NotEqual(occupiedAfterPort, pair.AfterPort);
        Assert.Equal(pair.AfterPort + 1, pair.BeforePort);
    }

    [Fact]
    public void AllocateComparison_Allows_Reusable_Listening_Pair()
    {
        using var listeners = StartAdjacentListeners();
        var afterPort = ((IPEndPoint)listeners.After.LocalEndpoint).Port;
        var sut = new TcpPreviewPortAllocator();

        var pair = sut.AllocateComparison(afterPort, [afterPort, afterPort + 1]);

        Assert.Equal(afterPort, pair.AfterPort);
        Assert.Equal(afterPort + 1, pair.BeforePort);
    }

    private static TcpListener StartListener(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    private static AdjacentListeners StartAdjacentListeners()
    {
        for (var port = 55000; port < 60000; port += 2)
        {
            TcpListener? after = null;
            TcpListener? before = null;
            try
            {
                after = StartListener(port);
                before = StartListener(port + 1);
                return new AdjacentListeners(after, before);
            }
            catch (SocketException)
            {
                after?.Stop();
                before?.Stop();
            }
        }

        throw new InvalidOperationException("Could not find adjacent test ports.");
    }

    private sealed class AdjacentListeners(TcpListener after, TcpListener before) : IDisposable
    {
        public TcpListener After { get; } = after;

        public void Dispose()
        {
            After.Stop();
            before.Stop();
        }
    }
}