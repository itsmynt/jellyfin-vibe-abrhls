using System.Diagnostics;
using System.Collections.Concurrent; // Neu für Thread-Safety
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaEncoding; // Neu

namespace Jellyfin.ABRHls.Services;

public class HlsPackager
{
    private readonly ILogger<HlsPackager> _log;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _mediaEncoder; // Neu: Jellyfins Encoder Info
    private readonly Plugin _plugin;

    // Verhindert doppelte Ausführung für denselben Film
    private static readonly ConcurrentDictionary<string, Task<bool>> _activeTasks = new();

    public HlsPackager(ILogger<HlsPackager> log, IApplicationPaths paths, ILibraryManager library, IMediaEncoder mediaEncoder)
    {
        _log = log;
        _paths = paths;
        _library = library;
        _mediaEncoder = mediaEncoder;
        _plugin = (Plugin)Plugin.Instance!;
    }

    public string GetOutputDir(Guid itemId, string profile = "default")
    {
        var cfg = _plugin.Configuration;
        var root = Path.IsPathRooted(cfg.OutputRoot) ? cfg.OutputRoot : Path.Combine(_paths.DataPath, cfg.OutputRoot);
        return Path.Combine(root, itemId.ToString(), profile);
    }

    // Wrapper methoden bleiben gleich...
    public Task<bool> EnsurePackedAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedInternalAsync(itemId, _plugin.Configuration.Ladder, "default", ct, false, false);

    public Task<bool> EnsurePackedFireTvSdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedInternalAsync(itemId, _plugin.Configuration.FireTvUhdSdr, "firetv_sdr", ct, true, false);

    public Task<bool> EnsurePackedFireTvHdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedInternalAsync(itemId, _plugin.Configuration.FireTvUhdHdr, "firetv_hdr", ct, true, true);

    private async Task<bool> EnsurePackedInternalAsync(Guid itemId, List<LadderProfile> ladder, string profileName, CancellationToken ct, bool fireTv, bool hdr)
    {
        // Lock-Key erstellen, damit wir wissen, was gerade bearbeitet wird
        string taskKey = $"{itemId}_{profileName}";

        // Wenn schon ein Task läuft, warten wir einfach auf dessen Ergebnis
        return await _activeTasks.GetOrAdd(taskKey, async (_) =>
        {
            try
            {
                return await RunFfmpegProcess(itemId, ladder, profileName, ct, fireTv, hdr);
            }
            finally
            {
                // Wenn fertig, entfernen wir den Lock
                _activeTasks.TryRemove(taskKey, out _);
            }
        });
    }

    private async Task<bool> RunFfmpegProcess(Guid itemId, List<LadderProfile> ladder, string profileName, CancellationToken ct, bool fireTv, bool hdr)
    {
        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) return false;

        var outDir = GetOutputDir(item.Id, profileName);
        var master = Path.Combine(outDir, "master.m3u8");

        // Wenn die Datei schon da ist, sind wir sofort fertig!
        if (File.Exists(master)) return true;

        Directory.CreateDirectory(outDir);
        var cfg = _plugin.Configuration;

        // 1. FFmpeg Pfad Auto-Detect
        var ff = !string.IsNullOrEmpty(cfg.FfmpegPath) ? cfg.FfmpegPath : _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ff))
        {
            _log.LogError("FFmpeg Pfad nicht gefunden! Bitte in Jellyfin prüfen.");
            return false;
        }

        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 10);
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", Quote(item.Path!) };
        var varMap = new List<string>();

        // Audio Analyse (vereinfacht für Stabilität)
        string? srcAcodec = item.AudioStream?.Codec?.ToLowerInvariant();
        int? srcChannels = item.AudioStream?.Channels;

        int aindex = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];

            // Audio-Only Spur Logic
            if (L.Name == "audio")
            {
                args.AddRange(new[] { "-map", "0:a:0?", $"-c:a:{aindex}", "aac", $"-b:a:{aindex}", "128k", $"-vn:{aindex}" });
                varMap.Add($"a:{aindex},name:audio");
                aindex++;
                continue;
            }

            // Video Spuren
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" }); // Map Video & erstes Audio

            // Codec Einstellungen
            if (L.CopyVideo) { args.AddRange(new[] { $"-c:v:{i}", "copy" }); }
            else
            {
                args.AddRange(new[] { $"-c:v:{i}", L.VideoCodec });
                // Hinzufügen von Pixel-Format für Kompatibilität
                if (L.VideoCodec == "libx264") args.AddRange(new[] { $"-pix_fmt:{i}", "yuv420p" });
                else if (L.VideoCodec == "hevc") args.AddRange(new[] { $"-pix_fmt:{i}", "yuv420p10le" }); // HDR/10bit Support

                args.AddRange(new[] { $"-b:v:{i}", L.Bitrate.ToString(), $"-maxrate:v:{i}", L.Maxrate.ToString(), $"-bufsize:v:{i}", L.Bufsize.ToString(),
                                      $"-preset:{i}", "veryfast", $"-g:{i}", (seg*24).ToString(), $"-sc_threshold:{i}", "0" });

                if (!L.UseOriginalResolution && L.Width > 0)
                    args.AddRange(new[] { $"-vf:{i}", $"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease" });
            }

            // Audio Einstellungen (Simplifiziert)
            args.AddRange(new[] { $"-c:a:{i}", "aac", $"-b:a:{i}", "128k", $"-ac:{i}", "2" });
            varMap.Add($"v:{i},a:{i},name:{L.Name}");
        }

        // HLS Settings
        var flags = cfg.UseFmp4 ? "independent_segments" : ""; // Vereinfachte Flags für Kompatibilität
        var segType = cfg.UseFmp4 ? "-hls_segment_type fmp4" : "";

        args.AddRange(new[] {
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", Quote(string.Join(' ', varMap)),
            "-hls_time", seg.ToString(),
            "-hls_playlist_type", "vod",
            "-f", "hls",
            "-hls_segment_filename", Quote(Path.Combine(outDir, "%v/seg_%06d.m4s")),
             Quote(Path.Combine(outDir, "%v/index.m3u8"))
        });

        if (!string.IsNullOrWhiteSpace(segType)) args.Insert(args.IndexOf("-f"), segType);

        Directory.CreateDirectory(Path.Combine(outDir, "%v"));

        var psi = new ProcessStartInfo(ff, string.Join(' ', args))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _log.LogInformation("Starte ABR Packaging für {ItemName}...", item.Name);

        using var p = Process.Start(psi);
        if (p == null) return false;

        // Timeout Schutz (z.B. wenn FFmpeg hängt)
        var waitTask = p.WaitForExitAsync(ct);
        if (await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromHours(4), ct)) != waitTask)
        {
            _log.LogError("FFmpeg Timeout - Prozess wird beendet.");
            p.Kill();
            return false;
        }

        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            _log.LogError("FFmpeg Fehler: {Err}", err);
            return false;
        }

        if (cfg.GenerateThumbnails)
            await GenerateWebVttThumbnailsAsync(ff, item, outDir, cfg.ThumbnailIntervalSeconds, cfg.ThumbnailWidth, ct);

        return File.Exists(master);
    }

    // Thumbnails Methode wie gehabt, nur Pfad fixen wir gleich beim Aufruf
    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct)
    {
        // Code hier ist okay, wurde oben durch ffmpeg Variable bereits optimiert
        // (Kurzfassung: Du kannst den alten Code hier lassen, er nutzt jetzt den richtigen Pfad)
        // ... der Rest der Methode aus deiner Originaldatei ...
        var thumbsDir = Path.Combine(outDir, "thumbs");
        Directory.CreateDirectory(thumbsDir);
        var pattern = Path.Combine(thumbsDir, "thumb_%05d.jpg");
        var args = $"-y -hide_banner -loglevel error -i {Quote(item.Path!)} -vf fps=1/{interval},scale={width}:-1 -q:v 7 {Quote(pattern)}";
        using (var p = Process.Start(new ProcessStartInfo(ffmpeg, args) { RedirectStandardError = true, RedirectStandardOutput = true }))
        { if (p != null) await p.WaitForExitAsync(ct); }

        var files = Directory.GetFiles(thumbsDir, "thumb_*.jpg").OrderBy(f => f).ToArray();
        var vtt = Path.Combine(outDir, "thumbnails.vtt");
        using var sw = new StreamWriter(vtt);
        sw.WriteLine("WEBVTT");
        for (int i = 0; i < files.Length; i++)
        {
            var start = TimeSpan.FromSeconds(i * interval);
            var end = TimeSpan.FromSeconds((i + 1) * interval);
            sw.WriteLine($"{FormatTs(start)} --> {FormatTs(end)}");
            sw.WriteLine($"thumbs/{Path.GetFileName(files[i])}");
            sw.WriteLine();
        }
    }

    private static string FormatTs(TimeSpan t) => $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.000";
    private static string Quote(string s) => $"\"{s}\"";
}