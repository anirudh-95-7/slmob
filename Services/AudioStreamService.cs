#if ANDROID
using Android.Media;
#endif

namespace SLMobileViewer.Services;

/// <summary>Plays the parcel/region audio stream (Shoutcast/Icecast MP3 URL).</summary>
public sealed class AudioStreamService
{
#if ANDROID
    private MediaPlayer? _player;
#endif

    public bool IsPlaying { get; private set; }
    public string? CurrentUrl { get; private set; }

    public async Task<string> PlayAsync(string url)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(url)) return "No audio stream set on this parcel.";

#if ANDROID
        try
        {
            _player = new MediaPlayer();
            _player.SetAudioAttributes(new AudioAttributes.Builder()!
                .SetContentType(AudioContentType.Music)!
                .SetUsage(AudioUsageKind.Media)!
                .Build()!);
            _player.SetDataSource(url);

            var tcs = new TaskCompletionSource<bool>();
            _player.Prepared += (s, e) => tcs.TrySetResult(true);
            _player.Error += (s, e) => tcs.TrySetResult(false);
            _player.PrepareAsync();

            var done = await Task.WhenAny(tcs.Task, Task.Delay(15000)).ConfigureAwait(false);
            if (done != tcs.Task || !tcs.Task.Result)
            {
                Stop();
                return "Stream did not respond (or format unsupported).";
            }

            _player.Start();
            IsPlaying = true;
            CurrentUrl = url;
            return $"Playing: {url}";
        }
        catch (Exception ex)
        {
            Stop();
            return $"Audio error: {ex.Message}";
        }
#else
        await Task.CompletedTask;
        return "Audio streaming is only supported on Android.";
#endif
    }

    public void Stop()
    {
#if ANDROID
        try
        {
            if (_player != null)
            {
                if (_player.IsPlaying) _player.Stop();
                _player.Release();
                _player.Dispose();
            }
        }
        catch { }
        _player = null;
#endif
        IsPlaying = false;
        CurrentUrl = null;
    }
}
