using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using AndroidX.Core.App;

namespace SLMobileViewer;

/// <summary>
/// Keeps the grid connection alive when the app is backgrounded or the screen is off.
/// Android Doze otherwise suspends the UDP socket and the session silently dies.
/// </summary>
[Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public class ConnectionService : Service
{
    private const string ChannelId = "slmobile_connection";
    private const int NotificationId = 4711;

    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("SL Mobile Viewer")
            .SetContentText("Connected to the grid")
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .Build();

        StartForeground(NotificationId, notification);
        AcquireLocks();
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        ReleaseLocks();
        base.OnDestroy();
    }

    private void CreateChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr?.GetNotificationChannel(ChannelId) != null) return;

        var channel = new NotificationChannel(ChannelId, "Grid connection", NotificationImportance.Low)
        {
            Description = "Keeps your Second Life session connected"
        };
        mgr?.CreateNotificationChannel(channel);
    }

    private void AcquireLocks()
    {
        try
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "slmobile:connection");
            _wakeLock?.Acquire();

            var wm = (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
            _wifiLock = wm?.CreateWifiLock(WifiMode.FullHighPerf, "slmobile:wifi");
            _wifiLock?.Acquire();
        }
        catch { }
    }

    private void ReleaseLocks()
    {
        try { if (_wakeLock?.IsHeld == true) _wakeLock.Release(); } catch { }
        try { if (_wifiLock?.IsHeld == true) _wifiLock.Release(); } catch { }
        _wakeLock = null;
        _wifiLock = null;
    }

    public static void Start(Context ctx)
    {
        var intent = new Intent(ctx, typeof(ConnectionService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
        else ctx.StartService(intent);
    }

    public static void Stop(Context ctx)
        => ctx.StopService(new Intent(ctx, typeof(ConnectionService)));
}
