namespace RepoSyncRadar.App.Components;

internal static class ProgressElapsedDisplay
{
    public static TimeSpan Advance(TimeSpan displayedElapsed, DateTimeOffset startedAt, DateTimeOffset now)
        => TimeSpan.FromSeconds(SmoothElapsedSeconds(
            (int)displayedElapsed.TotalSeconds,
            (int)(now - startedAt).TotalSeconds));

    public static int SmoothElapsedSeconds(int displayedElapsedSeconds, int actualElapsedSeconds)
    {
        if (actualElapsedSeconds <= displayedElapsedSeconds)
        {
            return displayedElapsedSeconds;
        }

        return displayedElapsedSeconds + 1;
    }
}