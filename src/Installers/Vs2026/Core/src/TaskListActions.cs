using System;
using Microsoft.VisualStudio.Shell;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Tasks the bridge puts in the Visual Studio Task List (addTask, clearTasks). They live in one <see cref="TaskProvider"/>
/// that the bridge owns, so clearing removes only the bridge's own tasks. The provider is created on first use, on the UI thread.
/// </summary>
internal static class TaskListActions
{
    private static TaskProvider? _provider;

    public static object AddTask(string text, string? file, int? line, string category, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var errorCategory = category switch
        {
            "Error" => TaskErrorCategory.Error,
            "Warning" => TaskErrorCategory.Warning,
            "Message" => TaskErrorCategory.Message,
            _ => throw new ArgumentException(Strings.Format("UnknownTaskCategory", category)),
        };

        var change = new { text, file, line, category };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        var task = new ErrorTask
        {
            Text = text,
            Category = TaskCategory.User,
            ErrorCategory = errorCategory,
        };
        if (!string.IsNullOrEmpty(file))
        {
            task.Document = file;
            // The Task List shows one-based lines; ErrorTask.Line is zero-based.
            task.Line = Math.Max(0, (line ?? 1) - 1);
        }

        Provider().Tasks.Add(task);
        return new { planned = false, applied = change };
    }

    public static object ClearTasks(bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var count = _provider?.Tasks.Count ?? 0;
        if (!confirm)
        {
            return new { planned = true, would = new { cleared = count } };
        }

        _provider?.Tasks.Clear();
        return new { planned = false, applied = new { cleared = count } };
    }

    private static TaskProvider Provider()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _provider ??= new TaskProvider(ServiceProvider.GlobalProvider)
        {
            ProviderName = "AiDevHelpers bridge",
        };
    }
}
