using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaEncoding; // Für den Encoder

namespace Jellyfin.ABRHls.Services;

public class HlsPackager
{
    private readonly ILogger<HlsPackager> _log;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _mediaEncoder; // Zugriff auf Jellyfins Encoder
    private readonly Plugin _plugin;

    public HlsPackager(ILogger<HlsPackager> log, IApplicationPaths paths, ILibraryManager library, IMediaEncoder mediaEncoder)
    { 
        _log = log; 
        _paths = paths; 
        _library = library; 
        _mediaEncoder = mediaEncoder;
        _plugin = Plugin.Instance!; 
    }

    public string GetOutputDir(Guid itemId, string profile = "default")
    {
        var cfg = _plugin.Configuration;
        // Fallback, falls Config leer ist
        string rootPath = "data/abrhls";
        if (!string.IsNullOrEmpty(cfg.OutputRoot)) rootPath = cfg.OutputRoot;

        var root = Path.IsPathRooted(rootPath) ? rootPath : Path.Combine(_paths.DataPath, rootPath);
        return Path.Combine(root, itemId.ToString("N"), profile);
    }

    public Task<bool> EnsurePackedAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.Ladder, "default", ct, fireTv:false, hdr:false);

    public Task<bool> EnsurePackedFireTvSdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.FireTvUhdSdr, "firetv_sdr", ct, fireTv:true, hdr:false);

    public Task<bool> EnsurePackedFireTvHdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.FireTvUhdHdr, "firetv_hdr", ct, fireTv:true, hdr:true);

    private async Task<bool> EnsurePackedAsync(Guid itemId, List<LadderProfile> ladder, string profileName, CancellationToken ct, bool fireTv, bool hdr)
    {
        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) 
        {
            _log.LogWarning("ABR: Item {Id} nicht gefunden oder kein Pfad.", itemId);
            return false;
        }

        var outDir = GetOutputDir(item.Id, profileName);
        
        // WICHTIG: Ordner erstellen und prüfen
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Konnte Ordner nicht erstellen {Dir}: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true;

        // --- FFmpeg Pfad Ermittlung ---
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;

        // 1. Versuch: Config
        if (string.IsNullOrWhiteSpace(ff)) 
        {
            // 2. Versuch: Jellyfin Internal
            ff = _mediaEncoder.EncoderPath;
        }

        if (string.IsNullOrWhiteSpace(ff))
        {
            // 3. Versuch: Systemweiter Befehl (Linux/Windows PATH)
            ff = "ffmpeg";
        }

        _log.LogInformation("ABR DEBUG: Nutze FFmpeg Pfad: '{Path}'", ff);

        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 6);
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", Quote(item.Path!) };
        var varMap = new List<string>();

        // Audio Stream sicher abrufen
        string? srcAcodec = null; int? srcChannels = 2;
        try
        {
            var audioStream = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault);
            if (audioStream == null) audioStream = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            
            if (audioStream != null)
            {
                srcAcodec = audioStream.Codec?.ToLowerInvariant();
                srcChannels = audioStream.Channels ?? 2;
            }
        }
        catch (Exception ex) { _log.LogWarning("Fehler beim Audio-Check: {Ex}", ex.Message); }

        int aindex = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            
            // Audio Only
            if (L.Name == "audio")
            {
                if (!cfg.AddStereoAacFallback) continue;
                args.AddRange(new[]{ "-map", "0:a:0?", $"-c:a:{aindex}", "aac", $"-b:a:{aindex}", Math.Max(96_000, L.AudioBitrate).ToString(), $"-vn:{aindex}" });
                varMap.Add($"a:{aindex},name:{L.Name}");
                aindex++;
                continue;
            }

            // Video
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });
            
            if (L.CopyVideo) { args.AddRange(new[] { $"-c:v:{i}", "copy" }); }
            else
            {
                var vcodec = L.VideoCodec;
                if (hdr && vcodec == "hevc") args.AddRange(new[] { $"-c:v:{i}", "hevc", $"-profile:v:{i}", "main10", $"-pix_fmt:{i}", "yuv420p10le" });
                else args.AddRange(new[] { $"-c:v:{i}", vcodec, $"-pix_fmt:{i}", "yuv420p" });
                
                args.AddRange(new[] { $"-b:v:{i}", L.Bitrate.ToString(), $"-maxrate:v:{i}", L.Maxrate.ToString(), $"-bufsize:v:{i}", L.Bufsize.ToString(),
                                      $"-preset:{i}", "veryfast", $"-g:{i}", (seg*24).ToString(), $"-sc_threshold:{i}", "0" });

                if (!L.UseOriginalResolution && L.Width > 0 && L.Height > 0)
                    args.AddRange(new[] { $"-vf:{i}", $"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease" });
            }

            // Audio Logic (vereinfacht)
            if (srcAcodec == "eac3" && cfg.KeepEac3IfPresent) args.AddRange(new[] { $"-c:a:{i}", "copy" });
            else args.AddRange(new[] { $"-c:a:{i}", "aac", $"-b:a:{i}", "128k", $"-ac:{i}", "2" });

            varMap.Add($"v:{i},a:{i},name:{L.Name}");
        }

        var flags = cfg.UseFmp4 ? "independent_segments" : "";
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

        // Unterordner erstellen
        foreach(var m in varMap)
        {
             // Extrahiere Namen (z.B. "name:1080p")
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null)
             {
                 var name = namePart.Substring(5);
                 Directory.CreateDirectory(Path.Combine(outDir, name));
             }
        }

        var psi = new ProcessStartInfo(ff, string.Join(' ', args)) { 
            RedirectStandardError = true, 
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = outDir 
        };

        _log.LogInformation("ABR DEBUG: Starte Prozess...");
        
        try 
        {
            using var p = Process.Start(psi);
            if (p == null) 
            {
                _log.LogError("ABR FEHLER: Process.Start gab null zurück.");
                return false;
            }

            // Wir lesen den Output asynchron, damit der Puffer nicht voll läuft
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            
            await p.WaitForExitAsync(ct);
            
            if (p.ExitCode != 0)
            {
                var err = await stderrTask;
                _log.LogError("ABR CRITICAL: FFmpeg ExitCode {Code}. Error Output:\n{Err}", p.ExitCode, err);
                return false;
            }
            
            _log.LogInformation("ABR ERFOLG: HLS Dateien erstellt in {Dir}", outDir);
        }
        catch (Exception ex)
        {
            _log.LogError("ABR EXCEPTION beim Starten von FFmpeg: {Msg}", ex.Message);
            return false;
        }

        if (cfg.GenerateThumbnails)
            await GenerateWebVttThumbnailsAsync(ff, item, outDir, cfg.ThumbnailIntervalSeconds, cfg.ThumbnailWidth, ct);

        return File.Exists(master);
    }

    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct)
    {
        try
        {
            var thumbsDir = Path.Combine(outDir, "thumbs");
            Directory.CreateDirectory(thumbsDir);
            var pattern = Path.Combine(thumbsDir, "thumb_%05d.jpg");
            var args = $"-y -hide_banner -loglevel error -i {Quote(item.Path!)} -vf fps=1/{interval},scale={width}:-1 -q:v 7 {Quote(pattern)}";
            
            var psi = new ProcessStartInfo(ffmpeg, args) { RedirectStandardError=true, RedirectStandardOutput=true, UseShellExecute=false, CreateNoWindow=true };
            using var p = Process.Start(psi);
            if(p!=null) await p.WaitForExitAsync(ct);
            
            // VTT schreiben... (Code gekürzt, da hier meist nicht der Fehler liegt)
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
        catch(Exception ex)
        {
            _log.LogError("ABR Thumbnail Fehler: {Msg}", ex.Message);
        }
    }

    private static string FormatTs(TimeSpan t) => $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.000";
    private static string Quote(string s) => $"\"{s}\"";
}
