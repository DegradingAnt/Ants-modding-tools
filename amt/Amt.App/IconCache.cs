using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Amt.App;

/// Streams mod icons (the CF avatar thumbnails the manifest carries) with a permanent disk cache, so the
/// first run trickles them down once and every later launch loads purely from disk. Decodes at 64px — the
/// table draws 32px, so 64 stays crisp on a 2× display without holding 800 full 256px bitmaps in memory.
/// Everything here is failure-tolerant: a bad URL / dead network / corrupt file just means "no icon" and the
/// row keeps its monogram fallback.
public static class IconCache
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools", "icons");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(6);   // polite: at most 6 concurrent downloads
    private static readonly ConcurrentDictionary<string, byte> Failed = new();   // don't re-hit dead URLs this run

    private static string PathFor(string url) =>
        Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img");

    /// Disk-or-network fetch, decoded to a small bitmap. Null = no icon (and the URL is remembered as failed
    /// for this run). Safe to call from any thread; the caller marshals the bitmap onto the UI thread.
    public static async Task<Bitmap?> GetAsync(string url)
    {
        if (url.Length == 0 || Failed.ContainsKey(url)) return null;
        var file = PathFor(url);
        try
        {
            if (!File.Exists(file))
            {
                await Gate.WaitAsync();
                try
                {
                    if (!File.Exists(file))   // another row may have fetched it while we queued
                    {
                        var bytes = await Http.GetByteArrayAsync(url);
                        Directory.CreateDirectory(Dir);
                        // write-then-move so a killed app never leaves a half-written cache entry
                        var tmp = file + ".tmp";
                        await File.WriteAllBytesAsync(tmp, bytes);
                        File.Move(tmp, file, overwrite: true);
                    }
                }
                finally { Gate.Release(); }
            }
            await using var fs = File.OpenRead(file);
            return Bitmap.DecodeToWidth(fs, 64);
        }
        catch
        {
            Failed[url] = 0;
            try { File.Delete(file); } catch { /* a corrupt cache entry shouldn't wedge the row */ }
            return null;
        }
    }
}
