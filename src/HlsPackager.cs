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

    public string GetOutputDir(BaseItem item, string profile = "default")
    {
        // 1. Priorität: Speichere direkt neben dem Film
        if (item != null && !string.IsNullOrEmpty(item.Path))
        {
            var movieDir = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(movieDir))
            {
                // Ziel: E:\Filme\MeinFilm\abr_hls\default
                return Path.Combine(movieDir, "abr_hls", profile);
            }
        }

        // 2. Fallback: Zentraler Speicherort aus Config oder Data-Ordner
        var cfg = _plugin.Configuration;
        string rootPath = "data/abrhls";
        if (!string.IsNullOrEmpty(cfg.OutputRoot)) rootPath = cfg.OutputRoot;

        var root = Path.IsPathRooted(rootPath) ? rootPath : Path.Combine(_paths.DataPath, rootPath);
        return Path.Combine(root, item?.Id.ToString("N") ?? "unknown", profile);
    }

    public Task<bool> EnsurePackedAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.Ladder, "default", ct, fireTv:false, hdr:false);

    public Task<bool> EnsurePackedFireTvSdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.FireTvUhdSdr, "firetv_sdr", ct, fireTv:true, hdr:false);

    public Task<bool> EnsurePackedFireTvHdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.FireTvUhdHdr, "firetv_hdr", ct, fireTv:true, hdr:true);

    private async Task<bool> EnsurePackedAsync(Guid itemId, List<LadderProfile> ladder, string profileName, CancellationToken ct, bool fireTv, bool hdr)
    {
        // Item laden und prüfen
        var baseItem = _library.GetItemById(itemId);
        if (baseItem is not Video item || string.IsNullOrEmpty(item.Path)) 
        {
            _log.LogWarning("ABR: Item {Id} ist kein Video oder hat keinen Pfad.", itemId);
            return false;
        }

        // Zielordner
        var outDir = GetOutputDir(item, profileName);
        
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Konnte Ordner {Dir} nicht erstellen: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true; // Schon fertig

        // FFmpeg Pfad
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = _mediaEncoder.EncoderPath; 
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg";

        // Medien-Analyse (Auflösung & Audio)
        int srcHeight = 1080;
        string? srcAcodec = null;

        try {
            if (item.Height.HasValue && item.Height > 0) srcHeight = item.Height.Value;
            
            // Sicherer Zugriff auf Streams
            var streams = item.GetMediaStreams();
            var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault) 
                     ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            srcAcodec = audio?.Codec?.ToLowerInvariant();
        } catch (Exception ex) {
            _log.LogWarning("ABR: Konnte Medieninfos nicht lesen, nutze Standards. {Ex}", ex.Message);
        }

        // Argumente bauen
        // WICHTIG: Pfade für Windows in Anführungszeichen!
        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        var varMap = new List<string>();
        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 6);

        int outputIndex = 0;

        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            
            // Audio only
            if (L.Name == "audio")
            {
                if (!cfg.AddStereoAacFallback) continue;
                args += $" -map 0:a:0? -c:a:{outputIndex} aac -b:a:{outputIndex} {Math.Max(96000, L.AudioBitrate)} -vn:{outputIndex}";
                varMap.Add($"a:{outputIndex},name:{L.Name}");
                outputIndex++;
                continue;
            }

            // FILTER: Kein Upscaling!
            if (!L.UseOriginalResolution && !L.CopyVideo)
            {
                if (L.Height > srcHeight) continue;
            }

            // Video Stream
            args += " -map 0:v:0 -map 0:a:0?";

            if (L.CopyVideo) { args += $" -c:v:{outputIndex} copy"; }
            else
            {
                var vcodec = L.VideoCodec;
                if (hdr && vcodec == "hevc") args += $" -c:v:{outputIndex} hevc -profile:v:{outputIndex} main10 -pix_fmt:{outputIndex} yuv420p10le";
                else args += $" -c:v:{outputIndex} {vcodec} -pix_fmt:{outputIndex} yuv420p";
                
                args += $" -b:v:{outputIndex} {L.Bitrate} -maxrate:v:{outputIndex} {L.Maxrate} -bufsize:v:{outputIndex} {L.Bufsize}";
                args += $" -preset:{outputIndex} veryfast -g:{outputIndex} {seg*24} -sc_threshold:{outputIndex} 0";

                if (!L.UseOriginalResolution && L.Width > 0 && L.Height > 0)
                    args += $" -vf:{outputIndex} \"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease\"";
            }

            // Audio Codec
            if (srcAcodec == "eac3" && cfg.KeepEac3IfPresent) args += $" -c:a:{outputIndex} copy";
            else args += $" -c:a:{outputIndex} aac -b:a:{outputIndex} 128k -ac:{outputIndex} 2";

            varMap.Add($"v:{outputIndex},a:{outputIndex},name:{L.Name}");
            outputIndex++;
        }

        if (outputIndex == 0) {
            _log.LogWarning("ABR: Keine Profile übrig (Videoauflösung zu niedrig?).");
            return false;
        }

        // HLS Master Playlist
        string segType = cfg.UseFmp4 ? "-hls_segment_type fmp4" : "";
        args += " -master_pl_name master.m3u8";
        args += " -var_stream_map \"" + string.Join(" ", varMap) + "\"";
        args += $" {segType} -f hls -hls_time {seg} -hls_playlist_type vod";
        
        // Dateinamen (mit Quotes!)
        string segExt = cfg.UseFmp4 ? "m4s" : "ts";
        string segPattern = Path.Combine(outDir, "%v", $"seg_%06d.{segExt}");
        string idxPattern = Path.Combine(outDir, "%v", "index.m3u8");
        
        args += $" -hls_segment_filename \"{segPattern}\" \"{idxPattern}\"";

        // Unterordner anlegen
        foreach(var m in varMap)
        {
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null) Directory.CreateDirectory(Path.Combine(outDir, namePart.Substring(5)));
        }

        // Prozess starten
        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = outDir 
        };

        _log.LogWarning("ABR START: {Item} -> {Dir}", item.Name, outDir);

        try 
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                var errOutput = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                
                if (p.ExitCode != 0)
                {
                    _log.LogError("ABR FEHLER {Code}:\n{Err}", p.ExitCode, errOutput);
                    return false;
                }
                _log.LogWarning("ABR ERFOLG: {Item}", item.Name);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("ABR CRASH: {Ex}", ex);
            return false;
        }

        if (cfg.GenerateThumbnails)
            await GenerateWebVttThumbnailsAsync(ff, item, outDir, cfg.ThumbnailIntervalSeconds, cfg.ThumbnailWidth, ct);

        return File.Exists(master);
    }

    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct)
    {
        // Optional: Thumbnail Logik hier einfügen (ebenfalls mit Quotes bei Pfaden!)
    }
}
