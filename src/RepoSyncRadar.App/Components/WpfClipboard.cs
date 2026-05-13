using System.Windows;
using System.Windows.Threading;

namespace RepoSyncRadar.App.Components;

/// <summary>
/// Production <see cref="IClipboard"/> backed by WPF's <see cref="Clipboard"/>. The set
/// is marshalled to the UI thread because WPF clipboard APIs are STA-only.
/// </summary>
public sealed class WpfClipboard : IClipboard
{
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Clipboard.SetText(text);
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                Clipboard.SetText(text);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }
}
