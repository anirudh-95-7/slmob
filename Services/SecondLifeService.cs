using System.Reflection;
using OpenMetaverse;

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

    private SecondLifeService()
    {
        Client = new GridClient();

        // Lightweight mobile profile
        Client.Settings.MULTIPLE_SIMS = false;
        Client.Settings.ALWAYS_DECODE_OBJECTS = true;
        Client.Settings.ALWAYS_REQUEST_OBJECTS = true;
        Client.Settings.OBJECT_TRACKING = true;
        Client.Settings.AVATAR_TRACKING = true;
        Client.Settings.SEND_AGENT_UPDATES = true;
        Client.Settings.STORE_LAND_PATCHES = false;
        Client.Settings.USE_ASSET_CACHE = false;
        Client.Settings.LOG_ALL_CAPS_ERRORS = false;
        Client.Settings.THROTTLE_OUTGOING_PACKETS = true;

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
    public Task<(bool ok, string message)> LoginAsync(string firstName, string lastName, string password, string startLocation)
    {
        return Task.Run(() =>
        {
            try
            {
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

                bool ok = Client.Network.Login(lp);
                if (ok)
                {
                    SetAdultMaturity();
                    CullEngine.Start();
                }
                return (ok, ok ? Client.Network.LoginMessage
                               : $"{Client.Network.LoginErrorKey}: {Client.Network.LoginMessage}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    /// <summary>
    /// Adult maturity handshake. LibreMetaverse has moved this API between versions
    /// (DirectoryManager.SetMaturityPreference vs AgentManager.SetAgentAccess), so we
    /// resolve whichever is present at runtime — guaranteeing this compiles on any 2.x/3.x.
    /// </summary>
    private void SetAdultMaturity()
    {
        try
        {
            var dir = Client.Directory;
            var m = dir.GetType().GetMethod("SetMaturityPreference");
            if (m != null)
            {
                var pType = m.GetParameters()[0].ParameterType;
                object arg = pType.IsEnum ? Enum.Parse(pType, "Adult") : "A";
                m.Invoke(dir, new[] { arg });
                return;
            }

            var self = Client.Self;
            var m2 = self.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .FirstOrDefault(x => x.Name == "SetAgentAccess");
            if (m2 != null)
            {
                var args = m2.GetParameters().Length == 1
                    ? new object?[] { "A" }
                    : new object?[] { "A", null };
                m2.Invoke(self, args);
            }
        }
        catch
        {
            // Maturity preference is best-effort; never fatal.
        }
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
