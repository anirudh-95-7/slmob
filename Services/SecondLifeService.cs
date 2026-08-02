using LibreMetaverse;

namespace SLMobileViewer.Services;

/// <summary>
/// Robust singleton wrapper around the LibreMetaverse GridClient.
/// All network callbacks are marshalled onto the MAUI UI thread.
/// </summary>
public sealed class SecondLifeService
{
    private static readonly Lazy<SecondLifeService> _instance = new(() => new SecondLifeService());
    public static SecondLifeService Instance => _instance.Value;

    public GridClient Client { get; }
    public SpatialCullEngine CullEngine { get; }

    public bool IsConnected => Client.Network.Connected;

    // UI-thread events
    public event Action<string>? ChatReceived;
    public event Action<ScriptDialogEventArgs>? ScriptDialogReceived;
    public event Action<string>? StatusChanged;

    private readonly List<string> _loginTrace = new();

    private SecondLifeService()
    {
        Client = new GridClient();

        // Diagnostic: record every login stage BEFORE the library's failure path
        // can overwrite the server's real message with "Canceled".
        Client.Network.LoginProgress += (s, e) =>
        {
            lock (_loginTrace)
                _loginTrace.Add($"{e.Status}{(string.IsNullOrEmpty(e.FailReason) ? "" : $" [{e.FailReason}]")}: {e.Message}");
        };

        // Lightweight mobile profile (LibreMetaverse 3.x settings layout)
        Settings.UserAgent = "SLMobileViewer/1.0";
        Client.Settings.Agent.MultipleSims = false;
        Client.Settings.Agent.SendUpdates = true;
        Client.Settings.World.AlwaysDecodeObjects = true;
        Client.Settings.World.AlwaysRequestObjects = true;
        Client.Settings.World.TrackObjects = true;
        Client.Settings.World.TrackAvatars = true;
        Client.Settings.World.StoreLandPatches = false;

        CullEngine = new SpatialCullEngine(Client);

        // ---- network callbacks -> UI thread ----
        Client.Self.ChatFromSimulator += (s, e) =>
        {
            if (string.IsNullOrEmpty(e.Message)) return;
            if (e.Type is ChatType.StartTyping or ChatType.StopTyping) return;
            var line = $"{e.FromName}: {e.Message}";
            MainThread.BeginInvokeOnMainThread(() => ChatReceived?.Invoke(line));
        };

        Client.Self.IM += (s, e) =>
        {
            if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent &&
                e.IM.Dialog != InstantMessageDialog.MessageFromObject) return;
            var line = $"[IM] {e.IM.FromAgentName}: {e.IM.Message}";
            MainThread.BeginInvokeOnMainThread(() => ChatReceived?.Invoke(line));
        };

        Client.Self.ScriptDialog += (s, e) =>
            MainThread.BeginInvokeOnMainThread(() => ScriptDialogReceived?.Invoke(e));

        Client.Network.Disconnected += (s, e) =>
            MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke($"Disconnected: {e.Message}"));

        Client.Network.SimConnected += (s, e) =>
            MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke($"Connected to {e.Simulator.Name}"));
    }

    /// <summary>Asynchronous login. Returns (success, message).</summary>
    public async Task<(bool ok, string message)> LoginAsync(string firstName, string lastName, string password, string startLocation)
    {
        try
        {
            // SL legacy protocol: only the first 16 chars of the password are hashed.
            if (password.Length > 16) password = password[..16];
            lock (_loginTrace) _loginTrace.Clear();

            var lp = Client.Network.DefaultLoginParams(
                firstName.Trim(), lastName.Trim(), password, "SLMobileViewer", "1.0");

            lp.Start = startLocation switch
            {
                "last" or "" or null => "last",
                "home" => "home",
                _ => startLocation.StartsWith("uri:", StringComparison.OrdinalIgnoreCase)
                        ? startLocation
                        : NetworkManager.StartLocation(startLocation, 128, 128, 25)
            };

            bool ok = await Client.Network.LoginAsync(lp).ConfigureAwait(false);
            if (ok)
            {
                await SetAdultMaturityAsync().ConfigureAwait(false);
                CullEngine.Start();
            }
            if (ok) return (true, Client.Network.LoginMessage);

            string trace;
            lock (_loginTrace) trace = string.Join("\n", _loginTrace);
            return (false, $"{Client.Network.LoginErrorKey}: {Client.Network.LoginMessage}\n-- server trace --\n{trace}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Adult maturity handshake ("A" = Adult access).</summary>
    private async Task SetAdultMaturityAsync()
    {
        try { await Client.Self.SetAgentAccessAsync("A").ConfigureAwait(false); }
        catch { /* best-effort; never fatal */ }
    }

    public void SendLocalChat(string message)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(message)) return;
        Client.Self.Chat(message, 0, ChatType.Normal);
    }

    /// <summary>Reply to an LSL llDialog blue menu.</summary>
    public void ReplyToScriptDialog(int channel, int buttonIndex, string buttonLabel, UUID objectID)
        => Client.Self.ReplyToScriptDialog(channel, buttonIndex, buttonLabel, objectID);

    public void Logout()
    {
        CullEngine.Stop();
        if (IsConnected) Client.Network.Logout();
    }
}
