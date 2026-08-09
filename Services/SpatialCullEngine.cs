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
    private readonly ConcurrentDictionary<UUID, Primitive.ObjectProperties> _props = new();
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
            if (e.Properties == null) return;
            _props[e.Properties.ObjectID] = e.Properties;
            if (!string.IsNullOrEmpty(e.Properties.Name))
                _names[e.Properties.ObjectID] = e.Properties.Name;
        };
        _client.Objects.ObjectProperties += (s, e) =>
        {
            if (e.Properties == null) return;
            _props[e.Properties.ObjectID] = e.Properties;
            if (!string.IsNullOrEmpty(e.Properties.Name))
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
        _props.Clear();
        _nameRequested.Clear();
    }

    /// <summary>Thread-safe geometry snapshot for the 3D renderer.</summary>
    public List<Primitive> SnapshotPrims() => _nearby.Values.ToList();

    /// <summary>
    /// Child prims in a linkset (and avatar attachments) store position/rotation
    /// RELATIVE to their parent. Resolve to world space, or fail if the parent
    /// hasn't arrived yet.
    /// </summary>
    public bool TryWorldTransform(Primitive p, out Vector3 pos, out Quaternion rot)
    {
        pos = p.Position;
        rot = p.Rotation;
        if (p.ParentID == 0) return true;

        var sim = _client.Network.CurrentSim;
        if (sim == null) return false;

        Primitive? parent = null;
        if (sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var pp)) parent = pp;
        else if (sim.ObjectsAvatars.TryGetValue(p.ParentID, out var av)) parent = av;
        if (parent == null) return false;

        var ppos = parent.Position;
        var prot = parent.Rotation;

        // one more level up, in case the parent is itself parented
        if (parent.ParentID != 0)
        {
            if (sim.ObjectsPrimitives.TryGetValue(parent.ParentID, out var gp) ||
                sim.ObjectsAvatars.TryGetValue(parent.ParentID, out var ga1))
            {
                var gp2 = sim.ObjectsPrimitives.TryGetValue(parent.ParentID, out var g1)
                    ? g1
                    : (sim.ObjectsAvatars.TryGetValue(parent.ParentID, out var g2) ? (Primitive)g2 : null);
                if (gp2 != null)
                {
                    var r = RotateVec(ppos, gp2.Rotation);
                    ppos = new Vector3(gp2.Position.X + r.X, gp2.Position.Y + r.Y, gp2.Position.Z + r.Z);
                    prot = MulQuat(gp2.Rotation, prot);
                }
            }
        }

        var rel = RotateVec(p.Position, prot);
        pos = new Vector3(ppos.X + rel.X, ppos.Y + rel.Y, ppos.Z + rel.Z);
        rot = MulQuat(prot, p.Rotation);
        return true;
    }

    /// <summary>World position, falling back to the raw value when unresolvable.</summary>
    public Vector3 WorldPos(Primitive p)
        => TryWorldTransform(p, out var pos, out _) ? pos : p.Position;

    /// <summary>True when this prim is attached to an avatar (handled by AvatarBaker).</summary>
    public bool IsAttachment(Primitive p)
    {
        if (p.ParentID == 0) return false;
        var sim = _client.Network.CurrentSim;
        return sim != null && sim.ObjectsAvatars.ContainsKey(p.ParentID);
    }

    internal static Vector3 RotateVec(Vector3 v, Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float ix = w * v.X + y * v.Z - z * v.Y;
        float iy = w * v.Y + z * v.X - x * v.Z;
        float iz = w * v.Z + x * v.Y - y * v.X;
        float iw = -x * v.X - y * v.Y - z * v.Z;
        return new Vector3(
            ix * w + iw * -x + iy * -z - iz * -y,
            iy * w + iw * -y + iz * -x - ix * -z,
            iz * w + iw * -z + ix * -y - iy * -x);
    }

    internal static Quaternion MulQuat(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public Primitive? FindByLocalId(uint localId)
        => _nearby.TryGetValue(localId, out var p) ? p : null;

    public Primitive.ObjectProperties? PropsFor(Primitive p)
        => _props.TryGetValue(p.ID, out var pr) ? pr : p.Properties;

    /// <summary>Ask the sim for full properties (description/owner) of one object.</summary>
    public void RequestDetails(Primitive p)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || p.ID == UUID.Zero) return;
        try { _client.Objects.RequestObjectPropertiesFamily(sim, p.ID); } catch { }
        try { _client.Objects.SelectObject(sim, p.LocalID); } catch { }
    }

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
        if (IsAttachment(prim)) return;                 // avatars handle their own attachments
        Vector3 avatarPos = AvatarPosition();
        if (!TryWorldTransform(prim, out var wpos, out _)) return;   // parent not here yet
        float distance = Vector3.Distance(avatarPos, wpos);

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
                if (IsAttachment(p)) continue;
                if (!TryWorldTransform(p, out var wp, out _)) continue;
                if (Vector3.Distance(avatarPos, wp) <= CullRadius)
                    _nearby[p.LocalID] = p;
            }
        }

        foreach (var kvp in _nearby)
        {
            var prim = kvp.Value;
            float d = Vector3.Distance(avatarPos, WorldPos(prim));
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
