using Blitztext.Core.Text;

namespace Blitztext.Speech;

public sealed class LocalTranscriptionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs the selected local model. Loads it once and keeps it ready; a second dictation during
/// a slow load waits for that load instead of starting another one.
/// </summary>
public sealed class LocalTranscriptionService(ModelStore store) : IDisposable
{
    public const string SlowPreparationMessage =
        "Sprachmodell wird vorbereitet. Beim ersten Mal dauert das einen Moment, bitte Blitztext nicht beenden.";

    private readonly object _lock = new();
    private ISpeechEngine? _engine;
    private string? _engineModelId;
    private Task<ISpeechEngine>? _loadTask;
    private string? _loadTaskModelId;

    public ModelStore Store => store;

    public bool IsReady(string modelId)
    {
        lock (_lock) return _engine is not null && _engineModelId == modelId;
    }

    public Task PrepareAsync(string modelId) => EngineAsync(modelId);

    /// <summary>Unloads the model, e.g. before it is deleted.</summary>
    public void Unload()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
            _engineModelId = null;
        }
    }

    /// <summary>
    /// Transcribes and calls <paramref name="onSlowPreparation"/> once if the model is still not
    /// ready after <paramref name="slowAfter"/>, so a first load does not look like a hang.
    /// </summary>
    public async Task<string> TranscribeAsync(
        float[] samples,
        string modelId,
        Action? onSlowPreparation = null,
        TimeSpan? slowAfter = null,
        CancellationToken cancellationToken = default)
    {
        using var watcherCancel = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            await Task.Delay(slowAfter ?? TimeSpan.FromSeconds(8), watcherCancel.Token);
            if (!IsReady(modelId)) onSlowPreparation?.Invoke();
        }, watcherCancel.Token);

        try
        {
            var engine = await EngineAsync(modelId);
            var text = await engine.TranscribeAsync(samples, cancellationToken);
            text = HallucinationFilter.RemovingTrailingCredits(text);
            if (text.Length == 0) throw new LocalTranscriptionException("Das lokale Modell hat keinen Text erkannt.");
            return text;
        }
        finally
        {
            watcherCancel.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
        }
    }

    private Task<ISpeechEngine> EngineAsync(string modelId)
    {
        lock (_lock)
        {
            if (_engine is not null && _engineModelId == modelId) return Task.FromResult(_engine);
            if (_loadTask is not null && _loadTaskModelId == modelId) return _loadTask;

            var model = LocalModelCatalog.Find(modelId);
            if (model is null || !store.IsInstalled(model))
            {
                throw new LocalTranscriptionException(
                    $"Lokales Sprachmodell fehlt: {model?.DisplayName ?? modelId}. Bitte in den Einstellungen laden.");
            }

            var task = Task.Run(() => Load(model));
            _loadTask = task;
            _loadTaskModelId = modelId;
            return Finish(task, modelId);
        }
    }

    private async Task<ISpeechEngine> Finish(Task<ISpeechEngine> task, string modelId)
    {
        try
        {
            var engine = await task;
            lock (_lock)
            {
                if (_engine is not null && !ReferenceEquals(_engine, engine)) _engine.Dispose();
                _engine = engine;
                _engineModelId = modelId;
                return engine;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_loadTaskModelId == modelId)
                {
                    _loadTask = null;
                    _loadTaskModelId = null;
                }
            }
        }
    }

    private ISpeechEngine Load(LocalModelInfo model)
    {
        try
        {
            return model.Engine switch
            {
                SpeechEngineKind.Parakeet => new ParakeetEngine(store),
                _ => new WhisperEngine(store.PathOf(model, model.Files[0].Name)),
            };
        }
        catch (Exception ex) when (ex is not LocalTranscriptionException)
        {
            throw new LocalTranscriptionException($"{model.DisplayName} konnte nicht geladen werden: {ex.Message}", ex);
        }
    }

    public void Dispose() => Unload();
}
