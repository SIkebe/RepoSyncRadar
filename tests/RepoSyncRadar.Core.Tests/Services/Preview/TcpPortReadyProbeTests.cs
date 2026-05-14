using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="TcpPortReadyProbe"/>. Verifies the small but important
/// invariants that <see cref="PreviewServerHost"/> relies on: detect a
/// listening port quickly, bail out on timeout when nothing listens, and abort
/// immediately when the child process dies before binding.
/// </summary>
public sealed class TcpPortReadyProbeTests
{
    [Fact]
    public async Task Returns_True_When_Port_Is_Listening()
    {
        // Bind a throwaway listener on an OS-chosen free port so the probe has a
        // real loopback target to connect to without coupling the test to a
        // hardcoded port number.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new TcpPortReadyProbe();

        var ready = await probe.WaitForListenAsync(
            port,
            TimeSpan.FromSeconds(5),
            processStillAlive: null,
            TestContext.Current.CancellationToken);

        Assert.True(ready);
    }

    [Fact]
    public async Task Returns_False_When_Timeout_Elapses()
    {
        // Pick a port nothing is listening on. There is a small race window where
        // another test or the OS could be using it, but a high private port
        // makes that astronomically unlikely.
        int port = FindFreePort();
        var probe = new TcpPortReadyProbe();

        var ready = await probe.WaitForListenAsync(
            port,
            TimeSpan.FromMilliseconds(800),
            processStillAlive: null,
            TestContext.Current.CancellationToken);

        Assert.False(ready);
    }

    [Fact]
    public async Task Aborts_Immediately_When_Process_Exits()
    {
        // Critical for fast failure UX: when npm crashes during `npm install`
        // verification we must not wait the full 4-minute timeout.
        int port = FindFreePort();
        var probe = new TcpPortReadyProbe();
        var aliveCheckCount = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ready = await probe.WaitForListenAsync(
            port,
            TimeSpan.FromMinutes(10),
            processStillAlive: () => { aliveCheckCount++; return false; },
            TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.False(ready);
        Assert.Equal(1, aliveCheckCount);
        // Should bail near-instantly — definitely not anywhere close to the 10 minute timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"probe took {sw.Elapsed}");
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
