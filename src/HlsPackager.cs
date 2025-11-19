using System.Diagnostics;
using System.Reflection; // Wichtig für den Fix
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaEncoding;
using Jellyfin.ABRHls;

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
        if (item != null && !string.IsNullOrEmpty(item.Path))
        {
            var movieDir = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(movieDir))
                return Path.Combine(movieDir, "abr_hls", profile);
        }
        
        var cfg = _plugin.Configuration;
        string rootPath = string.IsNullOrEmpty(cfg.OutputRoot) ? "data/abrhls" : cfg.OutputRoot;
        var root = Path.IsPathRooted(rootPath) ? rootPath : Path.Combine(_paths.DataPath, rootPath);
        return Path.Combine(root, item?.Id.ToString("N") ?? "unknown", profile);
    }

    public Task<bool> EnsurePackedAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.Ladder, "default", ct);

    private async Task<bool> EnsurePackedAsync(Guid itemId, List<LadderProfile> ladder, string profileName, CancellationToken ct)
    {
        var baseItem = _library.GetItemById(itemId);
        if (baseItem is not Video item || string.IsNullOrEmpty(item.Path)) return false;

        var outDir = GetOutputDir(item, profileName);
        try { Directory.CreateDirectory(outDir); } catch { return false; }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true;

        string ff = _plugin.Configuration.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = _mediaEncoder.EncoderPath ?? "ffmpeg";

        // --- SAFE MODE: Streams holen ---
        int srcHeight = 1080;
        string? srcAcodec = null;

        try 
        {
            // 1. Höhe sicher lesen (Property existiert immer)
            if (item.Height > 0) srcHeight = item.Height; // In 10.9 ist es int, in manchen Versionen int? - C# regelt das oft implizit, sonst casten.
            
            // 2. Streams sicher lesen (Der Crash-Fix)
            var streams = GetMediaStreamsSafe(item);
            
            if (streams != null)
            {
                var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault) 
                         ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
                srcAcodec = audio?.Codec?.ToLowerInvariant();
            }
        } 
        catch (Exception ex) 
        {
            _log.LogWarning("ABR: Medieninfo-Warnung: {Ex}", ex.Message);
        }
        // --------------------------------

        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        var varMap = new List<string>();
        var seg = Math.Clamp(_plugin.Configuration.SegmentDurationSeconds, 2, 6);

        int idx = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            
            if (L.Label == "audio") {
                if (!_plugin.Configuration.AddStereoAacFallback) continue;
                args += $" -map 0:a:0? -c:a:{idx} aac -b:a:{idx} 128k -vn:{idx}";
                varMap.Add($"a:{idx},name:{L.Label}");
                idx++; continue;
            }

            // Filter
            if (!L.UseOriginalResolution && !L.CopyVideo && L.Height > srcHeight) continue;

            args += " -map 0:v:0 -map 0:a:0?";
            if (L.CopyVideo) args += $" -c:v:{idx} copy";
            else {
                args += $" -c:v:{idx} {L.VideoCodec} -pix_fmt:{idx} yuv420p -b:v:{idx} {L.TargetBitrate}";
                args += $" -maxrate:v:{idx} {L.MaxBitrate} -bufsize:v:{idx} {L.MaxBitrate * 2}";
                args += $" -preset:{idx} veryfast -g:{idx} {seg*24} -sc_threshold:{idx} 0";
                if (!L.UseOriginalResolution && L.Width > 0) args += $" -vf:{idx} \"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease\"";
            }

            if (srcAcodec == "eac3" && _plugin.Configuration.KeepEac3IfPresent) args += $" -c:a:{idx} copy";
            else args += $" -c:a:{idx} aac -b:a:{idx} 128k -ac:{idx} 2";

            varMap.Add($"v:{idx},a:{idx},name:{L.Label}");
            idx++;
        }

        if (idx == 0) return false;

        string segType = _plugin.Configuration.UseFmp4 ? "-hls_segment_type fmp4" : "";
        string ext = _plugin.Configuration.UseFmp4 ? "m4s" : "ts";
        
        args += $" -master_pl_name master.m3u8 -var_stream_map \"{string.Join(" ", varMap)}\" {segType} -f hls -hls_time {seg} -hls_playlist_type vod";
        args += $" -hls_segment_filename \"{Path.Combine(outDir, "%v", $"seg_%06d.{ext}")}\" \"{Path.Combine(outDir, "%v", "index.m3u8")}\"";

        foreach(var m in varMap) {
             var n = m.Split(',').FirstOrDefault(x => x.StartsWith("name:"))?.Substring(5);
             if(n != null) Directory.CreateDirectory(Path.Combine(outDir, n));
        }

        var psi = new ProcessStartInfo { FileName = ff, Arguments = args, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = outDir };
        
        _log.LogWarning("ABR START: {Item} -> {Dir}", item.Name, outDir);
        
        try {
            using var p = Process.Start(psi);
            if (p != null) {
                var err = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0) { _log.LogError("ABR FEHLER {Code}:\n{Err}", p.ExitCode, err); return false; }
                _log.LogWarning("ABR FERTIG: {Item}", item.Name);
            }
        } catch (Exception ex) { _log.LogError("ABR CRASH: {Ex}", ex); return false; }

        return File.Exists(master);
    }

    // --- HELPER: Reflection für Kompatibilität ---
    private List<MediaStream>? GetMediaStreamsSafe(BaseItem item)
    {
        try 
        {
            // Versuch 1: Property "MediaStreams" (Jellyfin 10.10/10.11)
            var prop = item.GetType().GetProperty("MediaStreams");
            if (prop != null) return prop.GetValue(item) as List<MediaStream>;

            // Versuch 2: Methode "GetMediaStreams" (Jellyfin 10.8/10.9)
            var method = item.GetType().GetMethod("GetMediaStreams");
            if (method != null) return method.Invoke(item, null) as List<MediaStream>;
        }
        catch { /* Ignorieren */ }
        return null;
    }

    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct) { }
}
