using System.Windows;
using System.Windows.Threading;

namespace WallpaperStudio.Infrastructure;

/// <summary>
/// Background work (searches, downloads) must touch observable collections and
/// bindable properties on the UI thread. This keeps that hop in one place and
/// still works when there is no WPF application (unit tests, headless runs).
/// </summary>
public static class UiThread
{
    public static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    public static void Run(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    public static Task RunAsync(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
