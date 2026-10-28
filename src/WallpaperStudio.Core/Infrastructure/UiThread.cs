namespace WallpaperStudio.Infrastructure;

/// <summary>
/// Background work (searches, downloads) must touch observable collections and
/// bindable properties on the UI thread. Each UI head installs its own dispatcher
/// at startup, so the services stay free of a specific UI framework.
/// </summary>
public static class UiThread
{
    private static Func<Action, Task>? _post;
    private static Func<bool>? _isCurrent;

    /// <summary>Called by the UI layer once, before any view model runs.</summary>
    public static void Initialize(Func<Action, Task> post, Func<bool> isCurrent)
    {
        _post = post;
        _isCurrent = isCurrent;
    }

    public static void Run(Action action)
    {
        if (_post is null || _isCurrent?.Invoke() == true)
        {
            action();
            return;
        }

        _post(action).GetAwaiter().GetResult();
    }

    public static Task RunAsync(Action action)
    {
        if (_post is null || _isCurrent?.Invoke() == true)
        {
            action();
            return Task.CompletedTask;
        }

        return _post(action);
    }
}
