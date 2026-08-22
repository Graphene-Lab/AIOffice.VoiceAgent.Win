using System.Diagnostics;
using System.Speech.Synthesis;
using System.Text;
using AIOffice.VoiceAgent;

namespace AIOffice.VoiceAgent.Win;

/// <summary>
/// Windows voice agent executable — the Windows-specific subclass of
/// <see cref="VoiceAgentBase"/>: WinRT offline speech recognition + Kokoro TTS with the SAPI
/// fallback. Everything shared with the cross-platform agent (protocol loop, speak logic,
/// logging, render path) lives in the base; this class wires only the Windows pieces:
/// <see cref="WinRtRecognizer"/>, the SAPI fallback and the startup registry/privacy setup.
///
/// Protocol (stdin): {"cmd":"start"} | {"cmd":"speak","text":…,"lang":…,"streaming":bool}
///                    | {"cmd":"stop"}
/// Protocol (stdout): {"type":"ready","tts":…} | {"type":"transcript","text"}
///                    | {"type":"done"} | {"type":"error","text"}
/// </summary>
public class VoiceAgentWin : VoiceAgentBase
{
    /// <summary>True when Kokoro loaded successfully and is the primary TTS engine.</summary>
    private bool _ttsReady;

    /// <summary>Windows SAPI synthesizer, used as fallback when Kokoro is unavailable or the
    /// language is unsupported (file-based TTS — the sentence-chunked path stays parked).</summary>
    private SpeechSynthesizer? _fallbackSynth;

    /// <summary>
    /// Entry point. Supports --debug to attach a debugger and --tts-method full|fast (fast is
    /// the default). Initializes TTS, then enters the shared stdin command loop.
    /// </summary>
    public static async Task Main(string[] args)
    {
        AppContext.SetSwitch("System.Runtime.Serialization.EnableNewtonsoftJson", false);

        Log.Initialize(Log.IsEnabled);
        Log.LogStep("=== VoiceAgent (Windows) starting ===");
        LogThread("Main entry");

        // Force UTF-8 for stdin/stdout — the parent process sends/receives JSON-Lines over pipes,
        // typically with UTF-8 encoding. The default console code page (e.g. Windows-1252) mangles
        // UTF-8 content, including the BOM that some callers emit.
        Console.SetIn(new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true));
        Log.LogStep("UTF-8 stdin configured");

        var ttsMethod = "fast";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--tts-method" && args[i + 1] == "full")
                ttsMethod = "full";

        if (args.Contains("--debug"))
        {
            Debugger.Launch();
            // --debug is no longer passed by Voice.cs (DEBUG attach is handled
            // by VS multi-startup project configuration). Kept only for
            // manual debugging: run "VoiceAgent.exe --debug" from command line.
        }

        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Log.LogStep($"Working directory: {Environment.CurrentDirectory}");

        // Accept online speech privacy policy (required even for offline dictation on some Windows builds)
        try
        {
            var regPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy";
            Microsoft.Win32.Registry.SetValue(regPath, "HasAccepted", 1, Microsoft.Win32.RegistryValueKind.DWord);
            Log.LogStep("Privacy policy registry key set");
        }
        catch (Exception ex) { Log.LogStep($"Privacy key failed: {ex.Message}"); }

        // Check microphone privacy setting
        try
        {
            var micPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            var val = Microsoft.Win32.Registry.GetValue(micPath, "Value", null);
            Log.LogStep($"Microphone privacy: {val?.ToString() ?? "(null)"}");
        }
        catch (Exception ex) { Log.LogStep($"Mic privacy check: {ex.Message}"); }

        var agent = new VoiceAgentWin { TtsMethod = ttsMethod };
        await agent.RunAsync();
        Log.LogStep("=== VoiceAgent exited ===");
    }

    /// <summary>WinRT offline dictation recognizer (STA-thread based).</summary>
    protected override IAgentRecognizer CreateRecognizer() => new WinRtRecognizer();

    /// <summary>Continuous device sink (NAudio) for the streaming TTS path.</summary>
    protected override IAudioSink? CreateAudioSink() => new WindowsAudioSink();

    /// <summary>Waits for Kokoro (primary); when it is unavailable, sets up the SAPI fallback.
    /// If neither engine is available the startup fails (the base exits without the loop).</summary>
    protected override async Task InitializeTtsAsync()
    {
        Tts = new KokoroTts(s => WriteJson(new { type = "status", text = s }), TtsMethod);
        _ttsReady = await Tts.InitializeAsync();
        var ttsEngine = _ttsReady ? "kokoro" : "kokoro unavailable";
        Log.LogStep(_ttsReady ? "Kokoro TTS loaded successfully" : "Kokoro TTS failed, checking SAPI fallback");
        TtsTask = Task.FromResult(_ttsReady);

        if (!_ttsReady)
        {
            try
            {
                _fallbackSynth = new SpeechSynthesizer();
                ttsEngine = "sapi";
                Log.LogStep("SAPI fallback TTS ready");
            }
            catch (Exception fallbackEx)
            {
                WriteError($"No TTS available: {fallbackEx.Message}");
                Log.LogStep($"No fallback TTS either: {fallbackEx.Message}");
                return;   // StartupFailed stays true below
            }
        }
        _readyEngine = ttsEngine;
    }

    private string? _readyEngine;

    /// <summary>True when no TTS engine could be initialized (Kokoro + SAPI both failed) — the
    /// base exits without entering the command loop.</summary>
    protected override bool StartupFailed => !_ttsReady && _fallbackSynth == null;

    /// <summary>The "ready" payload reports which TTS engine is active.</summary>
    protected override object ReadyPayload() => new { type = "ready", tts = _readyEngine ?? "kokoro" };

    /// <summary>SAPI fallback — speaks with the Windows system synthesizer (file-based path).</summary>
    protected override bool TrySpeakOsFallback(string text, string? langCode)
    {
        if (_fallbackSynth == null) return false;
        Log.LogStep("Speaking with SAPI fallback");
        _fallbackSynth.Speak(text);
        return true;
    }

    /// <summary>Extra cleanup: disposes the SAPI synthesizer.</summary>
    protected override void StopAll()
    {
        base.StopAll();
        try { _fallbackSynth?.Dispose(); } catch { }
        _fallbackSynth = null;
    }

    /// <summary>Logs the current managed thread ID (useful for STA/MTA diagnostics).</summary>
    private static void LogThread(string label) =>
        Log.LogStep($"[THREAD] {label}: managedThreadId={Environment.CurrentManagedThreadId}");
}
