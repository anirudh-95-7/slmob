using System.Collections.Concurrent;
using LibreMetaverse;

namespace SLMobileViewer.Services;

public sealed record NearbyPrim(uint LocalID, string Name, float Distance);

/// <summary>
/// Spatial filter: tracks primitives inside a bubble around the avatar (20 m by
/// default, per spec). Anything beyond the radius is culled from memory.
/// Also resolves object names and provides geometry snapshots for the renderer.
/// </summary>
public sealed class SpatialCullEngine
{
    public const float DefaultRadius = 20.0f;

    /// <summary>Active cull radius in metres. Raise it to see more of the world.</summary>
    public float CullRadius { get; set; } = DefaultRadius;

    private readonly GridClient _client;
    private readonly ConcurrentDictionary<uint, Primitive> _nearby = new();
    private readonly ConcurrentDictionary<UUID, string> _names = new();
    private readonly ConcurrentDictionary<UUID, byte> _nameRequested = new();
    private Timer? _sweepTimer;
    private Vector3 _avatarPos = Vector3.Zero;

    /// <summary>UI-thread event: sorted snapshot of prims within the bubble.</summary>
    public event Action<IReadOnlyList<NearbyPrim>>? NearbyUpdated;

    public SpatialCullEngine(GridClient client)
    {
        _client = client;
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
        _client.Objects.KillObject += OnKillObject;

        _client.Objects.ObjectPropertiesFamily += (s, e) =>
        {
            if (e.Properties != null && !string.IsNullOrEmpty(e.Properties.Name))
                _names[e.Properties.ObjectID] = e.Properties.Name;
        };
        _client.Objects.ObjectProperties += (s, e) =>
        {
            if (e.Properties != null && !string.IsNullOrEmpty(e.Properties.Name))
                _names[e.Properties.ObjectID] = e.Properties.Name;
        };
    }

    public void Start() => _sweepTimer = new Timer(_ => Sweep(), null,
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    public void Stop()
    {
        _sweepTimer?.Dispose();
        _sweepTimer = null;
        _nearby.Clear();
        _names.Clear();
        _nameRequested.Clear();
    }

    /// <summary>Thread-safe geometry snapshot for the 3D renderer.</summary>
    public List<Primitive> SnapshotPrims() => _nearby.Values.ToList();

    public string NameFor(Primitive p)
    {
        if (_names.TryGetValue(p.ID, out var n) && !string.IsNullOrEmpty(n)) return n;
        if (p.Properties?.Name is { Length: > 0 } pn) return pn;
        return $"Object {p.LocalID}";
    }

    private void OnObjectUpdate(object? sender, PrimEventArgs e)
    {
        if (e.Prim is Avatar) return;
        Consider(e.Prim);
    }

    private void OnTerseObjectUpdate(object? sender, TerseObjectUpdateEventArgs e)
    {
        if (e.Update.Avatar && e.Prim.ID == _client.Self.AgentID)
        {
            _avatarPos = e.Update.Position;
            return;
        }
        if (e.Prim is Avatar) return;
        Consider(e.Prim);
    }

    private void OnKillObject(object? sender, KillObjectEventArgs e)
        => _nearby.TryRemove(e.ObjectLocalID, out _);

    /// <summary>SPATIAL RULE: retain only prims within the cull radius.</summary>
    private void Consider(Primitive prim)
    {
        Vector3 avatarPos = AvatarPosition();
        float distance = Vector3.Distance(avatarPos, prim.Position);

        if (distance <= CullRadius)
            _nearby[prim.LocalID] = prim;
        else
            _nearby.TryRemove(prim.LocalID, out _);
    }

    public Vector3 AvatarPosition()
    {
        var simPos = _client.Self.SimPosition;
        if (simPos != Vector3.Zero) _avatarPos = simPos;
        return _avatarPos;
    }

    /// <summary>Periodic purge + name resolution + UI snapshot.</summary>
    private void Sweep()
    {
        Vector3 avatarPos = AvatarPosition();
        var snapshot = new List<NearbyPrim>();
        var sim = _client.Network.CurrentSim;
        int requests = 0;

        // Pull anything the sim already knows about that we may have missed.
        if (sim != null)
        {
            foreach (var p in sim.ObjectsPrimitives.Values)
            {
                if (Vector3.Distance(avatarPos, p.Position) <= CullRadius)
                    _nearby[p.LocalID] = p;
            }
        }

        foreach (var kvp in _nearby)
        {
            var prim = kvp.Value;
            float d = Vector3.Distance(avatarPos, prim.Position);
            if (d > CullRadius)
            {
                _nearby.TryRemove(kvp.Key, out _);   // cull / purge from memory
                continue;
            }

            // Resolve real object names (throttled) instead of "Object 12345".
            if (sim != null && requests < 25 && !_names.ContainsKey(prim.ID)
                && prim.Properties == null && prim.ID != UUID.Zero
                && _nameRequested.TryAdd(prim.ID, 0))
            {
                try { _client.Objects.RequestObjectPropertiesFamily(sim, prim.ID); requests++; }
                catch { }
            }

            snapshot.Add(new NearbyPrim(kvp.Key, NameFor(prim), d));
        }

        snapshot.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        MainThread.BeginInvokeOnMainThread(() => NearbyUpdated?.Invoke(snapshot));
    }
}
