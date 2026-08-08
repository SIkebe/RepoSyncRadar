using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RepoSyncRadar.App;
using Xunit;

namespace RepoSyncRadar.App.Tests;

public sealed class AppShutdownTests
{
    [Fact]
    public void RegisterHostShutdown_WhenHostStops_DispatchesWindowClose()
    {
        using var applicationStopping = new CancellationTokenSource();
        var dispatched = false;
        var closeRequested = false;
        using var registration = App.RegisterHostShutdown(
            action =>
            {
                dispatched = true;
                action();
            },
            () => closeRequested = true,
            applicationStopping.Token);

        applicationStopping.Cancel();

        Assert.True(dispatched);
        Assert.True(closeRequested);
    }

    [Fact]
    public async Task ShutdownHostAsync_WaitsForStopAndDisposesAsyncServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Services.AddSingleton<AsyncDisposableTracker>();
        var host = builder.Build();
        var tracker = host.Services.GetRequiredService<AsyncDisposableTracker>();
        await host.StartAsync(TestContext.Current.CancellationToken);

        await App.ShutdownHostAsync(host, TimeSpan.FromSeconds(1));

        Assert.True(tracker.IsDisposed);
    }

    private sealed class AsyncDisposableTracker : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
