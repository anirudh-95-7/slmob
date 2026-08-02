using System.Collections.Concurrent;
using OpenMetaverse;

namespace SLMobileViewer.Services;

public sealed record NearbyPrim(uint LocalID, string Name, float Distance);

/// <summary>
/// Spatial filter: tracks primitives inside a strict 20-meter bubble around the
/// avatar. Anything beyond 20 m is culled from memory automatically.
/// </summary>
public sealed class SpatialCullEngine
{
    public const float CullRadius = 20.0f;

    private readonly GridClient _client;
    private readonly ConcurrentDictionary<uint, Primitive> _nearby = new();
    private Timer? _sweepTimer;
    private Vector3 _avatarPos = Vector3.Zero;

    /// <summary>UI-thread event: sorted snapshot of prims within the 20 m bubble.</summary>
    public event Action<IReadOnlyList<NearbyPrim>>? NearbyUpdated;

    public SpatialCullEngine(GridClient client)
    {
        _client = client;
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
        _client.Objects.KillObject += OnKillObject;
    }

    public void Start() => _sweepTimer = new Timer(_ => Sweep(), null,
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    public void Stop()
    {
        _sweepTimer?.Dispose();
        _sweepTimer = null;
        _nearby.Clear();
    }

    private void OnObjectUpdate(object? sender, PrimEventArgs e)
    {
        if (e.Prim is Avatar) return;
        Consider(e.Prim);
    }

    private void OnTerseObjectUpdate(object? sender, TerseObjectUpdateEventArgs e)
    {
        // Track our own avatar position from terse movement updates.
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

    /// <summary>SPATIAL RULE: retain only prims with distance <= 20.0f.</summary>
    private void Consider(Primitive prim)
    {
        Vector3 avatarPos = AvatarPosition();
        float distance = Vector3.Distance(avatarPos, prim.Position);

        if (distance <= CullRadius)
            _nearby[prim.LocalID] = prim;
        else
            _nearby.TryRemove(prim.LocalID, out _);
    }

    private Vector3 AvatarPosition()
    {
        var simPos = _client.Self.SimPosition;
        if (simPos != Vector3.Zero) _avatarPos = simPos;
        return _avatarPos;
    }

    /// <summary>Periodic purge: the avatar moves, so re-evaluate every tracked prim.</summary>
    private void Sweep()
    {
        Vector3 avatarPos = AvatarPosition();
        var snapshot = new List<NearbyPrim>();

        foreach (var kvp in _nearby)
        {
            float d = Vector3.Distance(avatarPos, kvp.Value.Position);
            if (d > CullRadius)
            {
                _nearby.TryRemove(kvp.Key, out _);   // cull / purge from memory
                continue;
            }
            string name = kvp.Value.Properties?.Name is { Length: > 0 } n
                ? n : $"Object {kvp.Key}";
            snapshot.Add(new NearbyPrim(kvp.Key, name, d));
        }

        snapshot.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        MainThread.BeginInvokeOnMainThread(() => NearbyUpdated?.Invoke(snapshot));
    }
}
