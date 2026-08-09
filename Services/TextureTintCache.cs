using System.Collections.Concurrent;
using LibreMetaverse;
using SkiaSharp;

namespace SLMobileViewer.Services;

/// <summary>
/// A software rasteriser can't map full textures per pixel, but it can use each
/// texture's average colour. That turns flat white geometry into recognisable
/// skin, clothing and furniture tones for a fraction of the cost.
/// </summary>
public sealed class TextureTintCache
{
    private readonly GridClient _client;
    private readonly ConcurrentDictionary<UUID, SKColor?> _cache = new();
    private readonly SemaphoreSlim _gate = new(3, 3);

    public TextureTintCache(GridClient client) => _client = client;

    public void Clear() => _cache.Clear();

    public bool TryGetCached(UUID id, out SKColor color)
    {
        color = SKColors.Gray;
        if (!_cache.TryGetValue(id, out var c) || c == null) return false;
        color = c.Value;
        return true;
    }

    public async Task<SKColor?> GetTintAsync(UUID textureId, CancellationToken token)
    {
        if (textureId == UUID.Zero) return null;
        if (_cache.TryGetValue(textureId, out var cached)) return cached;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(textureId, out cached)) return cached;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(10000);

            var tex = await _client.Assets.RequestImageAsync(textureId,
                ImageType.Normal, timeout.Token).ConfigureAwait(false);

            if (tex == null) { _cache[textureId] = null; return null; }
            if (tex.Image == null) { try { tex.Decode(); } catch { } }

            var img = tex.Image;
            if (img == null || img.Width <= 0 || img.Height <= 0)
            {
                _cache[textureId] = null;
                return null;
            }

            var avg = Average(img);
            _cache[textureId] = avg;
            return avg;
        }
        catch
        {
            _cache[textureId] = null;
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Average colour, skipping near-transparent pixels (alpha layers).</summary>
    private static SKColor? Average(LibreMetaverse.Imaging.ManagedImage img)
    {
        int n = Math.Min(img.Red.Length, img.Width * img.Height);
        if (n <= 0) return null;

        bool hasAlpha = img.Alpha.Length >= n;
        int step = Math.Max(1, n / 2048);
        long r = 0, g = 0, b = 0;
        int count = 0;

        for (int i = 0; i < n; i += step)
        {
            if (hasAlpha && img.Alpha[i] < 24) continue;   // transparent: ignore
            r += img.Red[i];
            g += img.Green.Length > i ? img.Green[i] : img.Red[i];
            b += img.Blue.Length > i ? img.Blue[i] : img.Red[i];
            count++;
        }
        if (count == 0) return null;

        return new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }
}
