using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using LibreMetaverse;

namespace SLMobileViewer.Services;

public sealed record NearbyAvatar(UUID Id, string Name, float Distance, Vector3 Position);
public sealed record FriendEntry(UUID Id, string Name, bool IsOnline);

/// <summary>One IM conversation with a single agent.</summary>
public sealed class ImThread
{
    public UUID AgentId { get; init; }
    public string Name { get; set; } = "";
    public ObservableCollection<string> Messages { get; } = new();
    public int Unread { get; set; }
}

/// <summary>People, friends, IM and parcel audio.</summary>
public sealed class WorldService
{
    private readonly GridClient _client;
    private readonly ConcurrentDictionary<UUID, string> _nameCache = new();
    private readonly ConcurrentDictionary<UUID, byte> _nameRequested = new();
    private readonly ConcurrentDictionary<UUID, ImThread> _threads = new();
    private Timer? _timer;

    public string? ParcelMusicUrl { get; private set; }
    public string ParcelName { get; private set; } = "";

    public event Action<IReadOnlyList<NearbyAvatar>>? AvatarsUpdated;
    public event Action<IReadOnlyList<FriendEntry>>? FriendsUpdated;
    public event Action<ImThread>? ImUpdated;
    public event Action<string>? Notice;

    public IReadOnlyCollection<ImThread> Threads => _threads.Values.ToList();

    public WorldService(GridClient client)
    {
        _client = client;

        _client.Avatars.UUIDNameReply += (s, e) =>
        {
            foreach (var kv in e.Names) _nameCache[kv.Key] = kv.Value;
        };

        _client.Friends.FriendOnline += (s, e) => PushFriends();
        _client.Friends.FriendOffline += (s, e) => PushFriends();

        _client.Friends.FriendshipOffered += (s, e) =>
            MainThread.BeginInvokeOnMainThread(() =>
                Notice?.Invoke($"Friendship offered by {e.AgentName}"));

        _client.Parcels.ParcelProperties += (s, e) =>
        {
            if (e.Parcel == null) return;
            ParcelMusicUrl = e.Parcel.MusicURL;
            ParcelName = e.Parcel.Name ?? "";
        };

        // Incoming IMs -> per-agent threads
        _client.Self.IM += (s, e) =>
        {
            var im = e.IM;
            if (im.Dialog != InstantMessageDialog.MessageFromAgent) return;
            if (im.FromAgentID == _client.Self.AgentID) return;

            var thread = GetOrCreateThread(im.FromAgentID, im.FromAgentName);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                thread.Messages.Add($"{im.FromAgentName}: {im.Message}");
                thread.Unread++;
                ImUpdated?.Invoke(thread);
            });
        };
    }

    public void Start()
    {
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _threads.Clear();
        _nameCache.Clear();
        _nameRequested.Clear();
    }

    public ImThread GetOrCreateThread(UUID agentId, string name)
        => _threads.GetOrAdd(agentId, id => new ImThread { AgentId = id, Name = name });

    public void SendIm(ImThread thread, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            _client.Self.InstantMessage(thread.AgentId, message);
            thread.Messages.Add($"You: {message}");
            ImUpdated?.Invoke(thread);
        }
        catch (Exception ex) { Notice?.Invoke($"IM failed: {ex.Message}"); }
    }

    public string NameFor(UUID id, string fallback = "")
    {
        if (_nameCache.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)) return n;
        return string.IsNullOrEmpty(fallback) ? id.ToString()[..8] : fallback;
    }

    /// <summary>Nearby avatar snapshot for the renderer (thread-safe).</summary>
    public List<NearbyAvatar> SnapshotAvatars(Vector3 selfPos, float radius)
    {
        var list = new List<NearbyAvatar>();
        var sim = _client.Network.CurrentSim;
        if (sim == null) return list;

        foreach (var av in sim.ObjectsAvatars.Values)
        {
            if (av.ID == _client.Self.AgentID) continue;
            float d = Vector3.Distance(selfPos, av.Position);
            if (d > radius) continue;

            string name = !string.IsNullOrEmpty(av.Name) ? av.Name : NameFor(av.ID);
            list.Add(new NearbyAvatar(av.ID, name, d, av.Position));
        }
        list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return list;
    }

    private void Tick()
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return;
        var selfPos = _client.Self.SimPosition;

        // People around us (uses a generous radius so the list is useful)
        var avatars = SnapshotAvatars(selfPos, 96f);

        // Resolve any unknown avatar names
        var missing = new List<UUID>();
        foreach (var av in sim.ObjectsAvatars.Values)
        {
            if (string.IsNullOrEmpty(av.Name) && !_nameCache.ContainsKey(av.ID)
                && _nameRequested.TryAdd(av.ID, 0))
                missing.Add(av.ID);
        }
        try
        {
            foreach (var f in _client.Friends.FriendList.Values)
            {
                if (string.IsNullOrEmpty(f.Name) && !_nameCache.ContainsKey(f.UUID)
                    && _nameRequested.TryAdd(f.UUID, 0))
                    missing.Add(f.UUID);
            }
        }
        catch { }

        if (missing.Count > 0)
        {
            try { _client.Avatars.RequestAvatarNames(missing); } catch { }
        }

        // Parcel audio info for the current position
        try
        {
            _client.Parcels.RequestParcelProperties(sim,
                selfPos.Y + 1, selfPos.X + 1, selfPos.Y - 1, selfPos.X - 1, -10000, false);
        }
        catch { }

        MainThread.BeginInvokeOnMainThread(() => AvatarsUpdated?.Invoke(avatars));
        PushFriends();
    }

    private void PushFriends()
    {
        var list = new List<FriendEntry>();
        try
        {
            foreach (var f in _client.Friends.FriendList.Values)
            {
                string name = !string.IsNullOrEmpty(f.Name) ? f.Name : NameFor(f.UUID);
                list.Add(new FriendEntry(f.UUID, name, f.IsOnline));
            }
        }
        catch { }

        list.Sort((a, b) => a.IsOnline == b.IsOnline
            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : (a.IsOnline ? -1 : 1));

        MainThread.BeginInvokeOnMainThread(() => FriendsUpdated?.Invoke(list));
    }
}
