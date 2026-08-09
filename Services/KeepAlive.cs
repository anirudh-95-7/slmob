namespace SLMobileViewer.Services;

/// <summary>Cross-platform hook for the Android foreground service.</summary>
public static class KeepAlive
{
    public static void Start()
    {
#if ANDROID
        try { ConnectionService.Start(Android.App.Application.Context); } catch { }
#endif
    }

    public static void Stop()
    {
#if ANDROID
        try { ConnectionService.Stop(Android.App.Application.Context); } catch { }
#endif
    }
}
