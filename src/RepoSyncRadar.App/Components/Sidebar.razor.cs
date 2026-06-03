using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.App.Components;

public partial class Sidebar
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "AuthChanged callback failed after {Operation}.")]
    private static partial void LogAuthChangedCallbackFailed(ILogger logger, Exception exception, string operation);
}
