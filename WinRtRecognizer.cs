using System.Text;
using Windows.Media.SpeechRecognition;
using Windows.System;
using AIOffice.VoiceAgent;

namespace AIOffice.VoiceAgent.Win;

/// <summary>
/// WinRT speech recognition (offline dictation) as an <see cref="IAgentRecognizer"/>, so the
/// Windows voice agent can reuse the shared <see cref="VoiceAgentBase"/> protocol/speak logic
/// (architectural rule: no redundant logic between the agents). WinRT requires STA-thread
/// operations: a dedicated <see cref="DispatcherQueueController"/> thread is created once and
/// every setup/recognition cycle runs on it.
/// </summary>
public sealed class WinRtRecognizer : IAgentRecognizer
{
    /// <summary>Raised with the recognized text.</summary>
    public event Action<string>? Transcript;

    /// <summary>Raised when recognition cannot continue.</summary>
    public event Action<string>? Error;

    /// <summary>WinRT captures from the microphone — external PCM is not supported (no-op).</summary>
    public bool ExternalInput { get; set; }

    private SpeechRecognizer? _recognizer;
    private CancellationTokenSource? _recognitionCts;
    private Task? _recognitionTask;
    private DispatcherQueueController? _dispatchController;

    /// <summary>Creates the dedicated STA thread used for all WinRT recognizer operations.</summary>
    public WinRtRecognizer()
    {
        try
        {
            _dispatchController = DispatcherQueueController.CreateOnDedicatedThread();
            Log.LogStep("DispatcherQueue STA thread created (WinRtRecognizer)");
        }
        catch (Exception ex)
        {
            Log.LogStep($"Failed to create DispatcherQueue: {ex.Message}");
        }
    }

    /// <summary>Starts recognition on the STA thread (stops any previous cycle first).</summary>
    public async Task StartAsync(string? lang)
    {
        Log.LogStep($"StartRecognitionAsync called (lang={lang ?? "default"})");

        StopRecognition();

        // If we have a DispatcherQueue, run recognizer setup on the STA thread.
        if (_dispatchController != null)
        {
            var queue = _dispatchController.DispatcherQueue;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool posted = queue.TryEnqueue(async () =>
            {
                try
                {
                    await SetupAndRunRecognizerAsync(lang);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Recognizer setup failed on STA thread: {ex.Message}");
                    Error?.Invoke($"Recognition failed: {ex.Message}");
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
            // Fallback: run directly on current thread.
            Log.LogStep("No DispatcherQueue, running directly");
            await SetupAndRunRecognizerAsync(lang);
        }
    }

    /// <summary>Cancels the ongoing recognition cycle (a later StartAsync resumes).</summary>
    public Task StopAsync()
    {
        StopRecognition();
        return Task.CompletedTask;
    }

    /// <summary>No-op — WinRT captures from the microphone (external PCM is whisper-only).</summary>
    public void FeedExternalPcm(byte[] pcm) { }

    /// <summary>Shuts down the STA thread.</summary>
    public void Dispose()
    {
        StopRecognition();
        _recognizer?.Dispose();
        _recognizer = null;
        try
        {
            _dispatchController?.ShutdownQueueAsync().AsTask().Wait(1000);
        }
        catch { }
        _dispatchController = null;
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

        // Compile
        Log.LogStep("Compiling constraints...");
        var compileResult = await _recognizer.CompileConstraintsAsync();
        Log.LogStep($"Compile result: {compileResult.Status}");

        if (compileResult.Status != SpeechRecognitionResultStatus.Success)
        {
            Error?.Invoke($"Compile failed: {compileResult.Status}");
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
                    result = await recognizerRef.RecognizeAsync().AsTask(token);
                    Log.LogStep($"RecognizeAsync #{attempt} returned: Status={result.Status}(raw={(int)result.Status}), Text='{result.Text}'");

                    if (result.Status == SpeechRecognitionResultStatus.Success)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Text))
                        {
                            var trimmed = result.Text.Trim();
                            Transcript?.Invoke(trimmed);
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
                    Error?.Invoke(ex.Message);
                    await Task.Delay(1000);
                    continue;
                }
            }

            Log.LogStep("Recognition loop exited");
        });
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

    /// <summary>Converts a two-letter ISO language code to a WinRT language tag (e.g. "it" → "it-IT").
    /// Returns null for unsupported or unrecognized codes.</summary>
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

    /// <summary>Logs the current managed thread ID (useful for STA/MTA diagnostics).</summary>
    private static void LogThread(string label) =>
        Log.LogStep($"[THREAD] {label}: managedThreadId={Environment.CurrentManagedThreadId}");
}
