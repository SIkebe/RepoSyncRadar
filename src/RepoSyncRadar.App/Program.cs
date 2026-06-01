using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Velopack;

namespace RepoSyncRadar.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => WindowsStartMenuShortcutRepair.Repair())
            .OnAfterUpdateFastCallback(_ => WindowsStartMenuShortcutRepair.Repair())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

internal static class WindowsStartMenuShortcutRepair
{
    private const string ShortcutName = "RepoSyncRadar.lnk";

    public static void Repair()
    {
        try
        {
            RepairCore();
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
        }
    }

    private static bool IsNonFatalException(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    private static void RepairCore()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return;
        }

        var startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(startMenuPrograms))
        {
            return;
        }

        var installRoot = ResolveInstallRoot(processPath);
        var shortcutTarget = ResolveShortcutTarget(processPath, installRoot);
        Directory.CreateDirectory(startMenuPrograms);

        var expectedShortcutPath = Path.Combine(startMenuPrograms, ShortcutName);
        try
        {
            RemoveStaleRepoSyncRadarShortcuts(startMenuPrograms, installRoot, expectedShortcutPath);
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
        }

        CreateShortcut(expectedShortcutPath, shortcutTarget, Path.GetDirectoryName(shortcutTarget)!, shortcutTarget);
    }

    private static string ResolveInstallRoot(string processPath)
    {
        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Path.GetFullPath(".");
        }

        return string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? directory
            : directory;
    }

    private static string ResolveShortcutTarget(string processPath, string installRoot)
    {
        var rootStub = Path.Combine(installRoot, "RepoSyncRadar.exe");
        return File.Exists(rootStub) ? rootStub : processPath;
    }

    private static void RemoveStaleRepoSyncRadarShortcuts(string startMenuPrograms, string installRoot, string expectedShortcutPath)
    {
        var installRootPrefix = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expectedFullPath = Path.GetFullPath(expectedShortcutPath);
        foreach (var shortcutPath in Directory.EnumerateFiles(startMenuPrograms, "*RepoSyncRadar*.lnk", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(shortcutPath), expectedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = TryGetShortcutTargetPath(shortcutPath);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                continue;
            }

            var targetFullPath = Path.GetFullPath(targetPath);
            if (targetFullPath.StartsWith(installRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(shortcutPath);
            }
        }
    }

    private static string? TryGetShortcutTargetPath(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateWScriptShell();
            shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath],
                culture: null);

            return shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null,
                culture: null) as string;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string iconPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateWScriptShell();
            shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath],
                culture: null);
            if (shortcut is null)
            {
                return;
            }

            SetShortcutProperty(shortcut, "TargetPath", targetPath);
            SetShortcutProperty(shortcut, "WorkingDirectory", workingDirectory);
            SetShortcutProperty(shortcut, "IconLocation", iconPath);
            shortcut.GetType().InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null,
                culture: null);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static object CreateWScriptShell()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new InvalidOperationException("WScript.Shell COM type was not found.");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell COM object could not be created.");
    }

    private static void SetShortcutProperty(object shortcut, string propertyName, string value)
        => shortcut.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: shortcut,
            args: [value],
            culture: null);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}