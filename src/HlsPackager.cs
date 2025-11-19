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
        // Versuch 1: Speichere direkt neben dem Film
        var item = _library.GetItemById(itemId);
        if (item != null && !string.IsNullOrEmpty(item.Path))
        {
            var movieDir = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(movieDir))
            {
                // Ergebnis: E:\Filme\MeinFilm\abr_hls\default
                return Path.Combine(movieDir, "abr_hls", profile);
            }
        }

        // Versuch 2 (Fallback): Zentraler Speicherort aus Config
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
        if (item == null || string.IsNullOrEmpty(item.Path)) return false;

        // Output Ordner bestimmen (jetzt neben dem Film)
        var outDir = GetOutputDir(item.Id, profileName);
        
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Ordnerfehler {Dir}: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        // Wenn fertig, nicht neu machen
        if (File.Exists(master)) return true;

        // FFmpeg Pfad holen
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg";

        // Argumente zusammenbauen
        // WICHTIG: Pfade in Anführungszeichen für Windows!
        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        var varMap = new List<string>();
        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 6);

        // Audio-Check (gibt es Surround?)
        string? srcAcodec = null; 
        try {
            var audio = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault) 
                     ?? item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            srcAcodec = audio?.Codec?.ToLowerInvariant();
        } catch {}

        // Die "Leiter" (Qualitäten) durchgehen
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            
            // Audio Only
            if (L.Name == "audio")
            {
                if (!cfg.AddStereoAacFallback) continue;
                args += $" -map 0:a:0? -c:a:{i} aac -b:a:{i} {Math.Max(96000, L.AudioBitrate)} -vn:{i}";
                varMap.Add($"a:{i},name:{L.Name}");
                continue;
            }

            // Video & Audio
            args += " -map 0:v:0 -map 0:a:0?";

            // Video Codec
            if (L.CopyVideo) { args += $" -c:v:{i} copy"; }
            else
            {
                var vcodec = L.VideoCodec;
                if (hdr && vcodec == "hevc") args += $" -c:v:{i} hevc -profile:v:{i} main10 -pix_fmt:{i} yuv420p10le";
                else args += $" -c:v:{i} {vcodec} -pix_fmt:{i} yuv420p";
                
                args += $" -b:v:{i} {L.Bitrate} -maxrate:v:{i} {L.Maxrate} -bufsize:v:{i} {L.Bufsize}";
                args += $" -preset:{i} veryfast -g:{i} {seg*24} -sc_threshold:{i} 0";

                if (!L.UseOriginalResolution && L.Width > 0 && L.Height > 0)
                    args += $" -vf:{i} \"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease\"";
            }

            // Audio Codec
            if (srcAcodec == "eac3" && cfg.KeepEac3IfPresent) args += $" -c:a:{i} copy";
            else args += $" -c:a:{i} aac -b:a:{i} 128k -ac:{i} 2";

            varMap.Add($"v:{i},a:{i},name:{L.Name}");
        }

        // HLS Grundeinstellungen
        string segType = cfg.UseFmp4 ? "-hls_segment_type fmp4" : "";
        args += " -master_pl_name master.m3u8";
        args += " -var_stream_map \"" + string.Join(" ", varMap) + "\"";
        args += $" {segType} -f hls -hls_time {seg} -hls_playlist_type vod";
        
        // Dateinamen (mit Quotes für Windows-Sicherheit!)
        string segExt = cfg.UseFmp4 ? "m4s" : "ts";
        string segPattern = Path.Combine(outDir, "%v", $"seg_%06d.{segExt}");
        string idxPattern = Path.Combine(outDir, "%v", "index.m3u8");
        
        args += $" -hls_segment_filename \"{segPattern}\" \"{idxPattern}\"";

        // Unterordner erstellen (1080p, 720p...)
        foreach(var m in varMap)
        {
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null) Directory.CreateDirectory(Path.Combine(outDir, namePart.Substring(5)));
        }

        // Prozessvorbereitung
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
                // Fehlerkanal lesen (FFmpeg schreibt alles dorthin)
                var errOutput = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                
                if (p.ExitCode != 0)
                {
                    _log.LogError("ABR CRASH bei {Item}. Code {Code}. Fehler:\n{Err}", item.Name, p.ExitCode, errOutput);
                    return false;
                }
                _log.LogWarning("ABR FERTIG: {Item}", item.Name);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("ABR SYSTEM-FEHLER: {Ex}", ex);
            return false;
        }

        if (cfg.GenerateThumbnails)
            await GenerateWebVttThumbnailsAsync(ff, item, outDir, cfg.ThumbnailIntervalSeconds, cfg.ThumbnailWidth, ct);

        return File.Exists(master);
    }

    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct)
    {
         // (Platzhalter: Thumbnail-Code kann hier bei Bedarf wieder rein, 
         //  muss aber auch "Process.Start" mit Quotes nutzen!)
    }
}
