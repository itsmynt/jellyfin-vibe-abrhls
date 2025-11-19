using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.ABRHls.Services;

public class HlsPackager
{
    private readonly ILogger<HlsPackager> _log;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _mediaEncoder;
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
            _log.LogWarning("ABR: Item {Id} nicht gefunden.", itemId);
            return false;
        }

        // 1. FFmpeg Pfad finden
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg";

        // WICHTIG: Log als Warning, damit es IMMER sichtbar ist
        _log.LogWarning("ABR DIAGNOSE: FFmpeg Pfad = '{Path}'", ff);
        _log.LogWarning("ABR DIAGNOSE: Video Pfad = '{Path}'", item.Path);

        var outDir = GetOutputDir(item.Id, profileName);
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Konnte Ordner nicht erstellen {Dir}: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true;

        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 6);
        // Fix für Windows Pfade mit Leerzeichen: Pfad in Anführungszeichen
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", $"\"{item.Path}\"" };
        var varMap = new List<string>();

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
        catch {}

        int aindex = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            if (L.Name == "audio")
            {
                if (!cfg.AddStereoAacFallback) continue;
                args.AddRange(new[]{ "-map", "0:a:0?", $"-c:a:{aindex}", "aac", $"-b:a:{aindex}", Math.Max(96_000, L.AudioBitrate).ToString(), $"-vn:{aindex}" });
                varMap.Add($"a:{aindex},name:{L.Name}");
                aindex++;
                continue;
            }

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

            if (srcAcodec == "eac3" && cfg.KeepEac3IfPresent) args.AddRange(new[] { $"-c:a:{i}", "copy" });
            else args.AddRange(new[] { $"-c:a:{i}", "aac", $"-b:a:{i}", "128k", $"-ac:{i}", "2" });

            varMap.Add($"v:{i},a:{i},name:{L.Name}");
        }

        var flags = cfg.UseFmp4 ? "independent_segments" : "";
        var segType = cfg.UseFmp4 ? "-hls_segment_type fmp4" : "";

        args.AddRange(new[] {
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", $"\"{string.Join(' ', varMap)}\"", // Zwingend Quotes für var_stream_map!
            "-hls_time", seg.ToString(),
            "-hls_playlist_type", "vod",
            "-f", "hls",
            "-hls_segment_filename", $"\"{Path.Combine(outDir, "%v/seg_%06d.m4s")}\"",
            $"\"{Path.Combine(outDir, "%v/index.m3u8")}\""
        });

        if (!string.IsNullOrWhiteSpace(segType)) args.Insert(args.IndexOf("-f"), segType);

        foreach(var m in varMap)
        {
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null) Directory.CreateDirectory(Path.Combine(outDir, namePart.Substring(5)));
        }

        // Argumente zu einem String zusammenbauen
        var argumentsString = string.Join(" ", args);
        _log.LogWarning("ABR DIAGNOSE: Starte FFmpeg mit Args: {Args}", argumentsString);

        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = argumentsString,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false, // Wichtig für Redirect
            CreateNoWindow = true,
            WorkingDirectory = outDir 
        };

        try 
        {
            using var p = Process.Start(psi);
            if (p == null) 
            {
                _log.LogError("ABR FEHLER: Process.Start ist NULL.");
                return false;
            }

            // Asynchrones Lesen des Error-Streams (dort landet FFmpeg Output)
            var errorOutput = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            
            if (p.ExitCode != 0)
            {
                _log.LogError("ABR FEHLER: FFmpeg ExitCode {Code}. Fehler: {Err}", p.ExitCode, errorOutput);
                return false;
            }
            
            _log.LogWarning("ABR ERFOLG: Dateien erstellt in {Dir}", outDir);
        }
        catch (Exception ex)
        {
            _log.LogError("ABR EXCEPTION: {Msg}", ex.Message);
            return false;
        }

        return File.Exists(master);
    }
    
    // Thumbnail-Methode (gekürzt, da hier meist nicht der Fehler liegt)
    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct) { }
}
