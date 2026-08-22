using AIOffice.VoiceAgent;
using NAudio.Wave;

namespace AIOffice.VoiceAgent.Win;

/// <summary>
/// <see cref="IAudioSink"/> for Windows: continuous device playback via NAudio
/// (<see cref="BufferedWaveProvider"/> + <see cref="WaveOutEvent"/>). The instance is REUSED
/// across streaming turns (created once, Start/EndAsync per turn) so a reply never pays the
/// ~300 ms WaveOutEvent setup; Dispose is called once when the conversation closes.
///
/// The tail drain of EndAsync is deferred (the device plays out the buffered audio), so a
/// session token guards the deferred stop: if a NEW turn starts before the drain completes, the
/// stale stop is skipped — the reused device keeps playing the new turn.
/// </summary>
public sealed class WindowsAudioSink : IAudioSink
{
    private readonly object _sync = new();
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _output;
    private bool _started;
    private bool _wroteFirst;   // diagnostic: logged the first PCM of this session
    private int _session;       // incremented on each Start; the deferred stop checks it

    /// <summary>Starts the output device for a new turn (idempotent within the turn).</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;   // already playing this turn
            _session++;
            _buffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
            {
                // Large buffer + no discard: the WHOLE reply is written back-to-back before the
                // device finishes draining; a small buffer (5 s) with DiscardOnBufferOverflow would
                // DROP the tail of any reply longer than the buffer → truncated audio.
                DiscardOnBufferOverflow = false,
                BufferDuration = TimeSpan.FromSeconds(120),
            };
            _output = new WaveOutEvent { DesiredLatency = 150 };
            _output.Init(_buffer);
            _output.Play();
            _started = true;
            _wroteFirst = false;
            Log.LogStep("WindowsAudioSink: device started (24 kHz streaming)");
        }
    }

    /// <summary>Appends one synthesized sentence to the continuous output buffer.</summary>
    public void Write(byte[] pcm24k)
    {
        if (pcm24k == null || pcm24k.Length == 0) return;
        lock (_sync)
        {
            // Diagnostic (first write of the session): time-to-first-audio = this log's timestamp
            // minus the "Speak: text_len=" log of the same turn.
            if (!_wroteFirst)
            {
                _wroteFirst = true;
                Log.LogStep($"WindowsAudioSink: first PCM written t={DateTime.UtcNow:HH:mm:ss.fff} ({pcm24k.Length} bytes)");
            }
            try { _buffer?.AddSamples(pcm24k, 0, pcm24k.Length); }
            catch (Exception ex) { Log.LogStep($"WindowsAudioSink write failed: {ex.Message}"); }
        }
    }

    /// <summary>Lets the buffered tail finish playing, then stops the device. The returned task
    /// completes when the device has stopped — the caller resumes recognition only afterwards.
    /// The drain is NOT capped at a few seconds (a short cap truncated the tail). The instance
    /// is reused: Start() of the next turn wins over any stale deferred stop.</summary>
    public Task EndAsync()
    {
        // 24 kHz × 2 bytes = 48000 bytes/s → ms = bufferedBytes / 48.
        int drainMs, session;
        lock (_sync)
        {
            session = _session;
            drainMs = (_buffer?.BufferedBytes ?? 0) / 48;
        }
        var delay = Math.Min(drainMs + 150, 120_000);   // +150 ms safety margin, 120 s hard ceiling
        return Task.Delay(delay).ContinueWith(_ => StopDeviceIfSession(session));
    }

    /// <summary>Stops the device immediately (no drain) — called when the conversation closes.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _session++;                       // invalidate any pending deferred stop
            _started = false;
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            _buffer = null;
        }
    }

    private void StopDeviceIfSession(int session)
    {
        lock (_sync)
        {
            if (session != _session) return;   // a new turn started — the reused device keeps playing
            _started = false;
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            _buffer = null;
        }
    }
}
