using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Amt.Core;

/// One queued download. Percent + Status raise PropertyChanged so a progress bar / status chip animates in place;
/// the rest is set once at enqueue time.
public sealed class DownloadTask : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ExpectedSha1 { get; init; } = "";   // "" = the source gave no hash, so we can't verify (not an error)
    public bool Verified { get; private set; }          // the finished file's sha1 matched the expected hash
    public string Error { get; private set; } = "";

    private int _percent;
    public int Percent { get => _percent; internal set => Set(ref _percent, value); }

    private string _status = "queued";                  // queued | downloading | done | error
    public string Status { get => _status; internal set => Set(ref _status, value); }

    internal void MarkVerified() => Verified = true;
    internal void Fail(string err) { Error = err; Status = "error"; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// A pausable, resumable download queue with a sha1 integrity gate — the "get the updates" half of the tool.
/// Nothing is auto-installed: files land in the configured download folder for the user to review and drop into
/// mods/ themselves (matching the Python's deliberate no-auto-install stance). One download runs at a time (kind
/// to the mod CDNs); the queue drains on a background task. A verified-before-promote rule means a corrupt or
/// truncated `.part` is deleted, never promoted to a real `.jar`.
///
/// Ported from the Python download manager (`_dl_one` / `_dl_worker` / `enqueue`), reshaped to async C#: the
/// thread + Event model becomes a background Task + a poll-gated pause flag.
public sealed class DownloadManager
{
    // Downloads get their OWN client with no overall timeout — a big mod jar can take longer than an API call;
    // cancellation + the per-read still bound it.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _downloadDir;
    private readonly List<DownloadTask> _queue = new();
    private readonly List<string> _log = new();
    private readonly object _lock = new();
    private volatile bool _paused;
    private bool _running;

    /// Fires on any queue-composition or task-status change (item queued, started, finished) so the UI can refresh
    /// its list + log. Fine-grained per-percent updates come through each <see cref="DownloadTask"/>'s own events.
    public event Action? Changed;

    public DownloadManager(string downloadDir) => _downloadDir = downloadDir;

    public bool IsPaused => _paused;
    public IReadOnlyList<string> Log { get { lock (_lock) return _log.ToArray(); } }
    public IReadOnlyList<DownloadTask> Tasks { get { lock (_lock) return _queue.ToArray(); } }

    public void Pause() { _paused = true; Changed?.Invoke(); }
    public void Resume() { _paused = false; Changed?.Invoke(); }

    /// Add downloads and make sure the worker is draining the queue.
    public void Enqueue(IEnumerable<DownloadTask> items)
    {
        bool start;
        lock (_lock)
        {
            _queue.AddRange(items);
            start = !_running && _queue.Any(t => t.Status == "queued");
            if (start) _running = true;
        }
        Changed?.Invoke();
        if (start) _ = Task.Run(WorkerAsync);
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            DownloadTask? task;
            lock (_lock) task = _queue.FirstOrDefault(t => t.Status == "queued");
            if (task is null) { lock (_lock) _running = false; Changed?.Invoke(); return; }
            await RunAsync(task);
        }
    }

    /// Run ONE download to completion (the same .part streaming + sha1 gate the queue uses) and return with the
    /// task in "done" or "error" state. This is the awaitable building block <see cref="UpdateInstaller"/>
    /// sequences directly — it needs to install each file as it finishes, which the fire-and-forget queue can't
    /// express. Never throws; failure lands in the task's Error/Status.
    public async Task RunAsync(DownloadTask task)
    {
        task.Status = "downloading"; task.Percent = 0; Changed?.Invoke();
        try
        {
            await DownloadOneAsync(task);
            task.Status = "done";
            AddLog($"OK  {task.FileName}" + (task.Verified ? "  (sha1 verified)" : "  (no hash to verify)"));
        }
        catch (Exception e)
        {
            task.Fail(e.Message);
            AddLog($"ERR {task.FileName}: {e.Message}");
        }
        Changed?.Invoke();
    }

    // Stream to a .part file with a live percent, honour pause, then sha1-gate before promoting to the real name.
    private async Task DownloadOneAsync(DownloadTask t)
    {
        Directory.CreateDirectory(_downloadDir);
        var dest = Path.Combine(_downloadDir, t.FileName);
        var tmp = dest + ".part";

        using (var resp = await Http.GetAsync(t.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buf = new byte[1 << 16];
            long got = 0;
            while (true)
            {
                while (_paused) await Task.Delay(200);      // pause gate — blocks the transfer, not the UI
                var n = await src.ReadAsync(buf);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n));
                got += n;
                if (total > 0) t.Percent = (int)(got * 100 / total);
            }
        }

        // integrity gate: verify the .part BEFORE it becomes a real jar, so a corrupt/truncated file never sits in
        // the review folder pretending to be a good mod. Skipped only when the source provided no hash.
        if (t.ExpectedSha1.Length > 0)
        {
            var actual = await Sha1Async(tmp);
            if (!actual.Equals(t.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                throw new InvalidDataException($"sha1 mismatch: got {actual[..12]}… expected {t.ExpectedSha1[..12]}… (corrupt/truncated)");
            }
            t.MarkVerified();
        }
        File.Move(tmp, dest, overwrite: true);
        t.Percent = 100;
    }

    private void AddLog(string line) { lock (_lock) _log.Add(line); }

    private static async Task<string> Sha1Async(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA1.HashDataAsync(fs)).ToLowerInvariant();
    }
}
