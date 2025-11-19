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
        // Speichere direkt neben dem Film
        var item = _library.GetItemById(itemId);
        if (item != null && !string.IsNullOrEmpty(item.Path))
        {
            var movieDir = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(movieDir))
            {
                return Path.Combine(movieDir, "abr_hls", profile);
            }
        }

        // Fallback
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

        var outDir = GetOutputDir(item.Id, profileName);
        
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Ordnerfehler {Dir}: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true; // Bereits fertig

        // FFmpeg Pfad
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg";

        // --- QUELL-ANALYSE ---
        // Wir holen uns die Auflösung des Originalvideos
        int srcHeight = 1080; // Standard Fallback
        int srcWidth = 1920;
        string? srcAcodec = null;

        try {
            var vStream = item.GetDefaultVideoStream();
            if (vStream != null)
            {
                srcHeight = vStream.Height ?? 1080;
                srcWidth = vStream.Width ?? 1920;
            }

            var audio = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault) 
                     ?? item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            srcAcodec = audio?.Codec?.ToLowerInvariant();
        } catch {}
        // ---------------------

        // Argumente bauen
        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        var varMap = new List<string>();
        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 6);

        int outputIndex = 0; // Zähler für die tatsächlich erstellten Streams

        // Die Leiter durchgehen und filtern
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            
            // 1. Audio immer mitnehmen
            if (L.Name == "audio")
            {
                if (!cfg.AddStereoAacFallback) continue;
                args += $" -map 0:a:0? -c:a:{outputIndex} aac -b:a:{outputIndex} {Math.Max(96000, L.AudioBitrate)} -vn:{outputIndex}";
                varMap.Add($"a:{outputIndex},name:{L.Name}");
                outputIndex++;
                continue;
            }

            // 2. FILTER: Wenn das Profil größer ist als die Quelle, überspringen!
            // Ausnahme: Wenn "UseOriginalResolution" an ist, oder "CopyVideo", dann behalten wir es (das ist meistens das Source-Profil)
            if (!L.UseOriginalResolution && !L.CopyVideo)
            {
                if (L.Height > srcHeight) 
                {
                    // Profil ist 1080p, aber Video ist nur 720p -> Überspringen
                    continue; 
                }
            }

            // Video Stream
            args += $" -map 0:v:0 -map 0:a:0?"; 

            if (L.CopyVideo) 
            { 
                args += $" -c:v:{outputIndex} copy"; 
            }
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

            // Audio Codec für diesen Videostream
            if (srcAcodec == "eac3" && cfg.KeepEac3IfPresent) args += $" -c:a:{outputIndex} copy";
            else args += $" -c:a:{outputIndex} aac -b:a:{outputIndex} 128k -ac:{outputIndex} 2";

            varMap.Add($"v:{outputIndex},a:{outputIndex},name:{L.Name}");
            outputIndex++;
        }

        if (varMap.Count == 0)
        {
            _log.LogWarning("ABR FEHLER: Keine Profile übrig nach Filterung (Videoauflösung zu niedrig?).");
            return false;
        }

        // HLS Settings
        string segType = cfg.UseFmp4 ? "-hls_segment_type fmp4" : "";
        args += $" -master_pl_name master.m3u8";
        args += $" -var_stream_map \"{string.Join(" ", varMap)}\"";
        args += $" {segType} -f hls -hls_time {seg} -hls_playlist_type vod";
        
        string segExt = cfg.UseFmp4 ? "m4s" : "ts";
        string segPattern = Path.Combine(outDir, "%v", $"seg_%06d.{segExt}");
        string idxPattern = Path.Combine(outDir, "%v", "index.m3u8");
        
        args += $" -hls_segment_filename \"{segPattern}\" \"{idxPattern}\"";

        // Unterordner erstellen
        foreach(var m in varMap)
        {
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null) Directory.CreateDirectory(Path.Combine(outDir, namePart.Substring(5)));
        }

        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = outDir 
        };

        _log.LogWarning("ABR START: {Item} ({W}x{H}) -> {Dir}", item.Name, srcWidth, srcHeight, outDir);

        try 
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                // Wir nutzen nun "outputIndex" als ID, aber im Log ist es egal.
                var errOutput = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                
                if (p.ExitCode != 0)
                {
                    _log.LogError("ABR CRASH: Code {Code}. Err:\n{Err}", p.ExitCode, errOutput);
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
         // Platzhalter für Thumbnails
    }
}
