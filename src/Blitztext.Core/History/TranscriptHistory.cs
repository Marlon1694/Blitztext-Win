using System.Text.Json;
using Blitztext.Core.Settings;
using Blitztext.Core.Workflows;

namespace Blitztext.Core.History;

public sealed record TranscriptHistoryEntry(Guid Id, DateTimeOffset CreatedAt, WorkflowType Workflow, string Text);

/// <summary>Encrypts and decrypts the history file; on Windows with DPAPI for the current user.</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] data);
    byte[]? Unprotect(byte[] data);
}

/// <summary>Recent results, stored only on this PC and encrypted for the signed-in Windows user.</summary>
public sealed class TranscriptHistoryStore
{
    public const int MaxEntries = 30;

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly Func<DateTimeOffset> _now;
    private HistoryRetention _retention;
    private List<TranscriptHistoryEntry> _entries = [];

    public event EventHandler? Changed;

    public IReadOnlyList<TranscriptHistoryEntry> Entries => _entries;

    public TranscriptHistoryStore(string path, IDataProtector protector, HistoryRetention retention, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _protector = protector;
        _retention = retention;
        _now = now ?? (() => DateTimeOffset.Now);
        _entries = Load();
        Prune();
    }

    public void Add(string text, WorkflowType workflow)
    {
        if (_retention == HistoryRetention.Off) return;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        _entries.Insert(0, new TranscriptHistoryEntry(Guid.NewGuid(), _now(), workflow, trimmed));
        Prune();
    }

    public void Remove(TranscriptHistoryEntry entry)
    {
        _entries.RemoveAll(e => e.Id == entry.Id);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _entries = [];
        TryDelete();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateRetention(HistoryRetention retention)
    {
        _retention = retention;
        Prune();
    }

    /// <summary>Drops expired entries. Called on launch, on every change and when the window opens.</summary>
    public void Prune()
    {
        if (_retention.MaxAge() is not { } maxAge)
        {
            if (_entries.Count > 0 || File.Exists(_path)) Clear();
            return;
        }
        var now = _now();
        var pruned = _entries.Where(e => now - e.CreatedAt < maxAge).Take(MaxEntries).ToList();
        var changed = !pruned.SequenceEqual(_entries);
        _entries = pruned;
        Save();
        if (changed || pruned.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        if (_entries.Count == 0)
        {
            TryDelete();
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = JsonSerializer.SerializeToUtf8Bytes(_entries, SettingsSerializer.Options);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, _protector.Protect(data));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a failed write must never block pasting.
        }
    }

    private List<TranscriptHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var data = _protector.Unprotect(File.ReadAllBytes(_path));
            if (data is null) return [];
            return JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(data, SettingsSerializer.Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void TryDelete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
