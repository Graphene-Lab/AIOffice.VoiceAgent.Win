using System.Diagnostics;
using System.Globalization;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using Windows.Media.SpeechRecognition;
using Windows.System;

namespace AIOffice.VoiceAgent.Win;

/// <summary>
/// Voice agent executable. Communicates via JSON Lines on stdin/stdout.
///
/// Protocol (stdin):
///   {"cmd":"start"}                         — begin speech recognition
///   {"cmd":"speak","text":"…","lang":"…"}   — speak text, then resume recognition
///   {"cmd":"stop"}                          — stop recognition and exit
///
/// Protocol (stdout):
///   {"type":"ready"}              — process initialized (tts field indicates engine)
///   {"type":"transcript","text"}  — user speech recognized
///   {"type":"done"}               — speak command finished
///   {"type":"error","text"}       — error occurred
///
/// Diagnostics are written to a file at {AppBase}/logs/{ProcessId}.txt when Log.IsEnabled is true.
/// Enable by passing --log on the command line, or set Log.IsEnabled = true programmatically.
/// </summary>
public class VoiceAgent
{
    /// <summary>TTS processing mode.</summary>
    public enum TtsMethod { Fast, Full }

    /// <summary>WinRT speech recognizer (offline dictation).</summary>
    private SpeechRecognizer? _recognizer;

    /// <summary>Kokoro neural TTS engine. Null when model loading failed.</summary>
    private KokoroTTS? _tts;

    /// <summary>Default Kokoro voice ("af_heart"), used when no language is specified.</summary>
    private KokoroVoice? _voice;

    /// <summary>Windows SAPI synthesizer, used as fallback when Kokoro is unavailable or language unsupported.</summary>
    private SpeechSynthesizer? _fallbackSynth;

    /// <summary>True when Kokoro loaded successfully and is the primary TTS engine.</summary>
    private bool _useKokoro;

    /// <summary>Cancels the ongoing RecognizeAsync call.</summary>
    private CancellationTokenSource? _recognitionCts;

    /// <summary>Cancels the main stdin loop, causing a graceful shutdown.</summary>
    private CancellationTokenSource? _shutdownCts;

    /// <summary>Background task running the recognition loop.</summary>
    private Task? _recognitionTask;

    /// <summary>True while a streaming speak session is active (recognition paused between chunks).</summary>
    private bool _streamingSession;

    /// <summary>TTS processing mode: Fast (SpeakFast, default) or Full (Speak).</summary>
    private TtsMethod _ttsMethod = TtsMethod.Fast;

    /// <summary>DispatcherQueue for STA-thread recognition operations.</summary>
    private DispatcherQueueController? _dispatchController;

    /// <summary>Language code passed with the last "start" command (e.g. "it"). Null = system default.</summary>
    private string? _recognitionLang;

    /// <summary>Logs the current managed thread ID (useful for STA/MTA diagnostics).</summary>
    private static void LogThread(string label) =>
        Log.LogStep($"[THREAD] {label}: managedThreadId={Environment.CurrentManagedThreadId}");

    // ─── Entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Entry point. Supports --debug to attach a debugger and --tts-method to
    /// select between "fast" (SpeakFast, default) and "full" (Speak).
    /// Step logging is auto-enabled in Debug builds (see <see cref="Log"/>).
    /// Initializes TTS, then enters the stdin command loop.
    /// </summary>
    public static async Task Main(string[] args)
    {
        AppContext.SetSwitch("System.Runtime.Serialization.EnableNewtonsoftJson", false);

        Log.Initialize(Log.IsEnabled);
        Log.LogStep("=== VoiceAgent starting ===");
        LogThread("Main entry");

        // Force UTF-8 for stdin/stdout — the parent process sends/receives JSON-Lines over pipes,
        // typically with UTF-8 encoding. The default console code page (e.g. Windows-1252) mangles
        // UTF-8 content, including the BOM that some callers emit.
        Console.SetIn(new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true));
        Log.LogStep("UTF-8 stdin configured");

        var ttsMethod = TtsMethod.Fast;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--tts-method" && args[i + 1] == "full")
                ttsMethod = TtsMethod.Full;

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

        var agent = new VoiceAgent { _ttsMethod = ttsMethod };
        await agent.RunAsync();
        Log.LogStep("=== VoiceAgent exited ===");
    }

    // ─── Main loop ────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        _shutdownCts = new CancellationTokenSource();
        Log.LogStep("Entering RunAsync");

        // Create the Dedicated STA Thread via DispatcherQueue for WinRT
        try
        {
            _dispatchController = DispatcherQueueController.CreateOnDedicatedThread();
            Log.LogStep("DispatcherQueue STA thread created");
            LogThread("After STA creation (main thread)");
            // Queue a one-shot to log the STA thread ID
            _dispatchController.DispatcherQueue.TryEnqueue(() => LogThread("STA thread (inside DispatcherQueue)"));
        }
        catch (Exception ex)
        {
            Log.LogStep($"Failed to create DispatcherQueue: {ex.Message}");
            // Continue without STA thread - may still work
        }

        // Try Kokoro neural TTS first
        string ttsEngine;
        try
        {
            var agentDir = Path.GetDirectoryName(typeof(VoiceAgent).Assembly.Location)!;
            Log.LogStep($"Agent directory: {agentDir}");
            var voicesDir = Path.Combine(agentDir, "voices");
            if (Directory.Exists(voicesDir))
            {
                KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
                Log.LogStep($"Voices loaded from: {voicesDir}");
            }

            _tts = KokoroTTS.LoadModel();
            _voice = KokoroVoiceManager.GetVoice("af_heart");
            _useKokoro = true;
            ttsEngine = "kokoro";
            Log.LogStep("Kokoro TTS loaded successfully");
        }
        catch (Exception kokoroEx)
        {
            Log.LogStep($"Kokoro failed: {kokoroEx.Message}, falling back to SAPI");
            try
            {
                _fallbackSynth = new SpeechSynthesizer();
                _useKokoro = false;
                ttsEngine = $"sapi ({kokoroEx.Message})";
                Log.LogStep("SAPI fallback TTS ready");
            }
            catch (Exception fallbackEx)
            {
                WriteJson(new { type = "error", text = $"No TTS available: {fallbackEx.Message}" });
                Log.LogStep($"No fallback TTS either: {fallbackEx.Message}");
                return;
            }
        }

        WriteJson(new { type = "ready", tts = ttsEngine });
        Log.LogStep($"Ready sent, TTS={ttsEngine}");

        try
        {
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync();
                if (line == null)
                {
                    Log.LogStep("stdin EOF, exiting main loop");
                    break;
                }

                Log.LogStep($"stdin cmd: {line}");

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch
                {
                    Log.LogStep($"Invalid JSON from stdin: {line}");
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
                    Log.LogStep($"Processing command: {cmd}");

                    switch (cmd)
                    {
                        case "start":
                            var startLang = root.TryGetProperty("lang", out var sl) ? sl.GetString() : null;
                            _recognitionLang = startLang;
                            await StartRecognitionAsync(startLang);
                            Log.LogStep($"StartRecognitionAsync completed (lang={startLang ?? "default"})");
                            break;

                        case "speak":
                            var text = root.TryGetProperty("text", out var t) ? t.GetString() : "";
                            var lang = root.TryGetProperty("lang", out var l) ? l.GetString() : null;
                            var streaming = root.TryGetProperty("streaming", out var s) && s.GetBoolean();
                            Log.LogStep($"Speak: lang={lang}, streaming={streaming}, text_len={text?.Length ?? 0}");
                            await SpeakAndPauseRecognitionAsync(text, lang, streaming);
                            WriteJson(new { type = "done" });
                            Log.LogStep("Speak done");
                            break;

                        case "stop":
                            Log.LogStep("Stop command received");
                            StopAll();
                            return;
                    }
                }
            }
        }
        finally
        {
            Log.LogStep("=== VoiceAgent shutting down ===");
            StopAll();
            _recognizer?.Dispose();
            _tts?.Dispose();
            _fallbackSynth?.Dispose();
            try
            {
                _dispatchController?.ShutdownQueueAsync().AsTask().Wait(1000);
                Log.LogStep("DispatcherQueue shut down");
            }
            catch { }
        }
    }

    // ─── Recognition ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts continuous speech recognition on the dedicated STA DispatcherQueue thread.
    /// </summary>
    /// <param name="langCode">Optional two-letter ISO language code (e.g. "it", "en").
    /// When null, uses the system default language.</param>
    private async Task StartRecognitionAsync(string? langCode = null)
    {
        Log.LogStep($"StartRecognitionAsync called (lang={langCode ?? "default"})");

        StopRecognition();

        // If we have a DispatcherQueue, run recognizer setup on the STA thread
        if (_dispatchController != null)
        {
            var queue = _dispatchController.DispatcherQueue;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool posted = queue.TryEnqueue(async () =>
            {
                try
                {
                    await SetupAndRunRecognizerAsync(langCode);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Recognizer setup failed on STA thread: {ex.Message}");
                    WriteJson(new { type = "error", text = $"Recognition failed: {ex.Message}" });
                    tcs.TrySetResult(false);
                }
            });

            if (posted)
            {
                await tcs.Task;
                Log.LogStep("Recognizer setup completed on STA thread");
            }
            else
                Log.LogStep("Failed to post to DispatcherQueue");
        }
        else
        {
            // Fallback: run directly on current thread
            Log.LogStep("No DispatcherQueue, running directly");
            await SetupAndRunRecognizerAsync(langCode);
        }
    }

    /// <summary>
    /// Sets up the SpeechRecognizer and starts the recognition loop.
    /// MUST be called from the STA DispatcherQueue thread when available.
    /// </summary>
    /// <param name="langCode">Optional two-letter ISO language code (e.g. "it", "en").
    /// When null, uses the system default language.</param>
    private async Task SetupAndRunRecognizerAsync(string? langCode = null)
    {
        Log.LogStep($"Setting up SpeechRecognizer (lang={langCode ?? "default"})");

        _recognizer?.Dispose();

        if (langCode != null)
        {
            // Convert 2-letter ISO code to WinRT language tag (e.g. "it" → "it-IT", "en" → "en-US")
            var winRtLang = GetWinRtLanguageTag(langCode);
            if (winRtLang != null)
            {
                try
                {
                    var language = new Windows.Globalization.Language(winRtLang);
                    _recognizer = new SpeechRecognizer(language);
                    Log.LogStep($"SpeechRecognizer created with language: {winRtLang}");
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Failed to create recognizer with lang '{winRtLang}': {ex.Message}. Falling back to default.");
                    _recognizer = new SpeechRecognizer();
                }
            }
            else
            {
                Log.LogStep($"Unsupported language code '{langCode}', using system default");
                _recognizer = new SpeechRecognizer();
            }
        }
        else
        {
            _recognizer = new SpeechRecognizer();
        }

        var langTag = _recognizer.CurrentLanguage?.LanguageTag ?? "(none)";
        Log.LogStep($"SpeechRecognizer language: {langTag}");

        // Log available recognizer languages
        try
        {
            var supportedLangs = SpeechRecognizer.SupportedGrammarLanguages?.ToList();
            Log.LogStep($"Supported grammar languages: {supportedLangs?.Count ?? 0}");
            if (supportedLangs != null)
                foreach (var lang in supportedLangs.Take(3))
                    Log.LogStep($"  - {lang.LanguageTag} ({lang.DisplayName})");
        }
        catch (Exception ex) { Log.LogStep($"Error listing languages: {ex.Message}"); }

        _recognizer.Constraints.Add(
            new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        Log.LogStep("Dictation constraint added");

        // Increase timeouts for a more forgiving listening experience
        _recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(15);
        _recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(500);
        _recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(0);
        Log.LogStep($"Timeouts: initialSilence={_recognizer.Timeouts.InitialSilenceTimeout.TotalSeconds}s " +
                     $"endSilence={_recognizer.Timeouts.EndSilenceTimeout.TotalMilliseconds}ms");

        // Compile
        Log.LogStep("Compiling constraints...");
        var compileResult = await _recognizer.CompileConstraintsAsync();
        Log.LogStep($"Compile result: {compileResult.Status}");

        if (compileResult.Status != SpeechRecognitionResultStatus.Success)
        {
            WriteError($"Compile failed: {compileResult.Status}");
            Log.LogStep($"COMPILE FAILED: {compileResult.Status}");
            return;
        }

        _recognitionCts = new CancellationTokenSource();
        var token = _recognitionCts.Token;
        var recognizerRef = _recognizer;

        Log.LogStep("Starting recognition loop");
        LogThread("Before Task.Run (should be STA or calling thread)");

        // Start a continuous recognition loop on the STA thread
        _recognitionTask = Task.Run(async () =>
        {
            Log.LogStep("Recognition loop task started");
            LogThread("Inside Task.Run (recognition loop)");
            int attempt = 0;

            while (!token.IsCancellationRequested)
            {
                attempt++;
                SpeechRecognitionResult result;
                try
                {
                    Log.LogStep($"RecognizeAsync call #{attempt} starting...");
                    result = await recognizerRef.RecognizeAsync()
                        .AsTask(token);
                    Log.LogStep($"RecognizeAsync #{attempt} returned: Status={result.Status}(raw={(int)result.Status}), Text='{result.Text}'");

                    if (result.Status == SpeechRecognitionResultStatus.Success)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Text))
                        {
                            var trimmed = result.Text.Trim();
                            WriteJson(new { type = "transcript", text = trimmed });
                            Log.LogStep($"TRANSCRIPT: '{trimmed}'");
                        }
                        else
                        {
                            Log.LogStep("Success but empty text");
                        }
                    }
                    else
                    {
                        var statusRaw = (int)result.Status;
                        Log.LogStep($"Non-success: status={result.Status}(raw={statusRaw}) text='{result.Text}'");
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.LogStep("Recognition loop cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Log.LogStep($"RecognizeAsync error: {ex.GetType().Name}: {ex.Message}");
                    WriteError(ex.Message);
                    await Task.Delay(1000);
                    continue;
                }
            }

            Log.LogStep("Recognition loop exited");
        });
    }

    // ─── TTS ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops recognition, speaks the text, then restarts recognition.
    /// Uses Kokoro neural TTS when available, otherwise falls back to Windows SAPI.
    /// </summary>
    private async Task SpeakAndPauseRecognitionAsync(string? text, string? langCode = null, bool streaming = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.LogStep("Speak skipped: empty text");
            return;
        }

        text = StripMarkdown(text);
        var effectiveLang = langCode ?? _recognitionLang;
        Log.LogStep($"Speak: text_len={text.Length}, lang={effectiveLang ?? "default"}, streaming={streaming}");

        if (streaming)
        {
            if (!_streamingSession)
            {
                _streamingSession = true;
                Log.LogStep("First streaming chunk: pausing recognition");
                StopRecognition();
                await Task.Delay(300);
            }
        }
        else if (_streamingSession)
        {
            _streamingSession = false;
            Log.LogStep("Final streaming chunk: ending streaming session");
        }
        else
        {
            Log.LogStep("Non-streaming speak: stopping recognition");
            StopRecognition();
            await Task.Delay(300);
        }

        KokoroVoice? voice = null;
        bool langUnsupported = false;

        if (effectiveLang != null)
        {
            voice = GetVoiceForLanguage(effectiveLang);
            langUnsupported = voice == null;
            Log.LogStep($"Language {effectiveLang}: voice={(voice != null ? "found" : "unsupported")}");
        }

        if (voice != null && _useKokoro && _tts != null)
        {
            Log.LogStep("Speaking with Kokoro (language-specific voice)");
            await SpeakWithKokoroAsync(text, voice);
        }
        else if (!langUnsupported && _useKokoro && _tts != null && _voice != null)
        {
            Log.LogStep("Speaking with Kokoro (default voice)");
            await SpeakWithKokoroAsync(text, _voice);
        }
        else if (_fallbackSynth != null)
        {
            Log.LogStep("Speaking with SAPI fallback");
            _fallbackSynth.Speak(text);
        }

        if (!streaming)
        {
            Log.LogStep("Non-streaming speak: restarting recognition after 500ms delay");
            await Task.Delay(500);
            await StartRecognitionAsync(_recognitionLang);
            Log.LogStep("Recognition restarted after speak");
        }
    }

    private async Task SpeakWithKokoroAsync(string text, KokoroVoice voice)
    {
        Log.LogStep($"Kokoro TTS starting (method={_ttsMethod})");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(SpeechCompletionPacket _) => tcs.TrySetResult();
        void OnCanceled(SpeechCancellationPacket _) => tcs.TrySetResult();
        _tts!.OnSpeechCompleted += OnCompleted;
        _tts.OnSpeechCanceled += OnCanceled;
        try
        {
            if (_ttsMethod == TtsMethod.Full)
                _tts.Speak(text, voice);
            else
                _tts.SpeakFast(text, voice);
            await tcs.Task;
            Log.LogStep("Kokoro TTS completed");
        }
        finally
        {
            _tts.OnSpeechCompleted -= OnCompleted;
            _tts.OnSpeechCanceled -= OnCanceled;
        }
    }

    private static KokoroVoice? GetVoiceForLanguage(string langCode)
    {
        var voiceName = langCode switch
        {
            "it" => "if_sara",
            "en" => "af_heart",
            "fr" => "ff_siwis",
            "es" => "ef_dora",
            "ja" => "jf_alpha",
            "zh" => "zf_xiaobei",
            "hi" => "hf_alpha",
            "pt" => "pf_dora",
            _ => null,
        };
        if (voiceName == null) return null;
        try { return KokoroVoiceManager.GetVoice(voiceName); }
        catch { return null; }
    }

    /// <summary>
    /// Converts a two-letter ISO language code to a WinRT language tag (e.g. "it" → "it-IT").
    /// Returns null for unsupported or unrecognized codes.
    /// </summary>
    private static string? GetWinRtLanguageTag(string langCode)
    {
        return langCode.ToLowerInvariant() switch
        {
            "it" => "it-IT",
            "en" => "en-US",
            "fr" => "fr-FR",
            "es" => "es-ES",
            "de" => "de-DE",
            "pt" => "pt-BR",
            "ja" => "ja-JP",
            "zh" => "zh-CN",
            "ru" => "ru-RU",
            "ar" => "ar-SA",
            "nl" => "nl-NL",
            "pl" => "pl-PL",
            "tr" => "tr-TR",
            _ => null,
        };
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string StripMarkdown(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]*)\]\([^)]+\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*\*(.+?)\*\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"~~(.+?)~~", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^>\s?", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\-\*\+]\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\d+\.\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\-\*\s_]{3,}$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\|-+\|", "");
        // Trattamento newline: se non preceduto da punteggiatura (. ! ?) diventa ", ",
        // altrimenti lascia un singolo \n come pausa di fine frase.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![.?!])\n", ", ");
        // Dopo la conversione, compatta spaziature multiple
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        text = FilterSpeakableChars(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string FilterSpeakableChars(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(text, i);
            if (cat == UnicodeCategory.UppercaseLetter || cat == UnicodeCategory.LowercaseLetter ||
                cat == UnicodeCategory.TitlecaseLetter || cat == UnicodeCategory.ModifierLetter ||
                cat == UnicodeCategory.OtherLetter || cat == UnicodeCategory.DecimalDigitNumber ||
                cat == UnicodeCategory.LetterNumber || cat == UnicodeCategory.OtherNumber ||
                cat == UnicodeCategory.SpaceSeparator || cat == UnicodeCategory.LineSeparator ||
                cat == UnicodeCategory.ParagraphSeparator)
            {
                result.Append(text[i]);
            }
            else if (cat == UnicodeCategory.DashPunctuation || cat == UnicodeCategory.OpenPunctuation ||
                     cat == UnicodeCategory.ClosePunctuation || cat == UnicodeCategory.InitialQuotePunctuation ||
                     cat == UnicodeCategory.FinalQuotePunctuation || cat == UnicodeCategory.OtherPunctuation)
            {
                result.Append(text[i]);
            }
            else if (cat == UnicodeCategory.CurrencySymbol || cat == UnicodeCategory.MathSymbol)
            {
                result.Append(text[i]);
            }
            else if (cat == UnicodeCategory.Surrogate)
            {
                i++;
            }
        }
        return result.ToString();
    }

    private void StopRecognition()
    {
        if (_recognitionCts != null)
        {
            Log.LogStep("StopRecognition: cancelling CTS");
            _recognitionCts.Cancel();
            _recognitionCts.Dispose();
            _recognitionCts = null;
        }
        _recognitionTask = null;
        Log.LogStep("StopRecognition complete");
    }

    private void StopAll()
    {
        Log.LogStep("StopAll");
        StopRecognition();
        _shutdownCts?.Cancel();
    }

    private static void WriteJson(object obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void WriteError(string message)
    {
        WriteJson(new { type = "error", text = message });
    }
}
