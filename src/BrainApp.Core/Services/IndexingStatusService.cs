using System.Collections.Concurrent;

namespace BrainApp.Core.Services;

/// <summary>
/// In-memory progress tracker for long-running indexing jobs kicked off from the API.
/// The Reindex endpoint returns 202 Accepted immediately; clients poll
/// GET /profiles/{id}/indexing-status to see how far the background job has gotten.
/// </summary>
public class IndexingStatusService
{
    public record Snapshot(
        bool Running,
        int Total,
        int Completed,
        int Failed,
        string? CurrentFile,
        double Percent,
        DateTime UpdatedAt);

    private readonly ConcurrentDictionary<string, State> _byProfile = new();

    private class State
    {
        public bool Running;
        public int Total;
        public int Completed;
        public int Failed;
        public string? CurrentFile;
        public double Percent;
        public DateTime UpdatedAt = DateTime.UtcNow;
        public readonly object Sync = new();
    }

    public void Start(string profileId, int total)
    {
        var s = _byProfile.GetOrAdd(profileId, _ => new State());
        lock (s.Sync)
        {
            s.Running = true;
            s.Total = total;
            s.Completed = 0;
            s.Failed = 0;
            s.CurrentFile = null;
            s.Percent = 0;
            s.UpdatedAt = DateTime.UtcNow;
        }
    }

    public void Report(string profileId, string? currentFile, double percent)
    {
        if (!_byProfile.TryGetValue(profileId, out var s)) return;
        lock (s.Sync)
        {
            s.CurrentFile = currentFile;
            s.Percent = percent;
            s.UpdatedAt = DateTime.UtcNow;
        }
    }

    public void IncrementCompleted(string profileId)
    {
        if (!_byProfile.TryGetValue(profileId, out var s)) return;
        lock (s.Sync)
        {
            s.Completed++;
            s.UpdatedAt = DateTime.UtcNow;
        }
    }

    public void IncrementFailed(string profileId)
    {
        if (!_byProfile.TryGetValue(profileId, out var s)) return;
        lock (s.Sync)
        {
            s.Failed++;
            s.UpdatedAt = DateTime.UtcNow;
        }
    }

    public void Finish(string profileId)
    {
        if (!_byProfile.TryGetValue(profileId, out var s)) return;
        lock (s.Sync)
        {
            s.Running = false;
            s.Percent = 100;
            s.CurrentFile = null;
            s.UpdatedAt = DateTime.UtcNow;
        }
    }

    public Snapshot Get(string profileId)
    {
        if (!_byProfile.TryGetValue(profileId, out var s))
            return new Snapshot(false, 0, 0, 0, null, 0, DateTime.UtcNow);
        lock (s.Sync)
        {
            return new Snapshot(s.Running, s.Total, s.Completed, s.Failed, s.CurrentFile, s.Percent, s.UpdatedAt);
        }
    }
}
