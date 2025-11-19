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
                return Path.Combine(movieDir, "abr_hls", profile);
            }
        }

        // 2. Fallback
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
        var baseItem = _library.GetItemById(itemId);
        if (baseItem is not Video item || string.IsNullOrEmpty(item.Path)) return false;

        var outDir = GetOutputDir(item, profileName);
        
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Ordnerfehler {Dir}: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true;

        string ff = _plugin.Configuration.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg";

        // --- FIX: Jellyfin 10.10 Height/Width sind int, nicht int? ---
        int srcHeight = 1080;
        string? srcAcodec = null;

        try {
            // Einfacher Check auf > 0 statt .HasValue
            if (item.Height > 0) srcHeight = item.Height;
            
            var streams = item.GetMediaStreams();
            var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault) 
                     ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            srcAcodec = audio?.Codec?.ToLowerInvariant();
        } catch {}
        // -----------------------------------------------------------

        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        var varMap = new List<string>();
        var seg = Math.Clamp(_plugin.Configuration.SegmentDurationSeconds, 2, 6);

        int idx = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];
            
            if (L.Name == "audio")
            {
                if (!_plugin.Configuration.AddStereoAacFallback) continue;
                args += $" -map 0:a:0? -c:a:{idx} aac -b:a:{idx} {Math.Max(96000, L.AudioBitrate)} -vn:{idx}";
                varMap.Add($"a:{idx},name:{L.Name}");
                idx++;
                continue;
            }

            // Filterung: Überspringen wenn Profil größer als Original
            if (!L.UseOriginalResolution && !L.CopyVideo)
            {
                if (L.Height > srcHeight) continue;
            }

            args += " -map 0:v:0 -map 0:a:0?";

            if (L.CopyVideo) { args += $" -c:v:{idx} copy"; }
            else
            {
                var vcodec = L.VideoCodec;
                if (hdr && vcodec == "hevc") args += $" -c:v:{idx} hevc -profile:v:{idx} main10 -pix_fmt:{idx} yuv420p10le";
                else args += $" -c:v:{idx} {vcodec} -pix_fmt:{idx} yuv420p";
                
                args += $" -b:v:{idx} {L.Bitrate} -maxrate:v:{idx} {L.Maxrate} -bufsize:v:{idx} {L.Bufsize}";
                args += $" -preset:{idx} veryfast -g:{idx} {seg*24} -sc_threshold:{idx} 0";

                if (!L.UseOriginalResolution && L.Width > 0 && L.Height > 0)
                    args += $" -vf:{idx} \"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease\"";
            }

            if (srcAcodec == "eac3" && _plugin.Configuration.KeepEac3IfPresent) args += $" -c:a:{idx} copy";
            else args += $" -c:a:{idx} aac -b:a:{idx} 128k -ac:{idx} 2";

            varMap.Add($"v:{idx},a:{idx},name:{L.Name}");
            idx++;
        }

        if (idx == 0) return false;

        string segType = _plugin.Configuration.UseFmp4 ? "-hls_segment_type fmp4" : "";
        args += $" -master_pl_name master.m3u8 -var_stream_map \"{string.Join(" ", varMap)}\" {segType} -f hls -hls_time {seg} -hls_playlist_type vod";
        
        string ext = _plugin.Configuration.UseFmp4 ? "m4s" : "ts";
        args += $" -hls_segment_filename \"{Path.Combine(outDir, "%v", $"seg_%06d.{ext}")}\" \"{Path.Combine(outDir, "%v", "index.m3u8")}\"";

        foreach(var m in varMap)
        {
             var p = m.Split(',');
             var n = p.FirstOrDefault(x => x.StartsWith("name:"));
             if(n != null) Directory.CreateDirectory(Path.Combine(outDir, n.Substring(5)));
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

        _log.LogWarning("ABR START: {Item} -> {Dir}", item.Name, outDir);

        try 
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                var err = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0) _log.LogError("ABR FEHLER {Code}:\n{Err}", p.ExitCode, err);
                else _log.LogWarning("ABR FERTIG: {Item}", item.Name);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("ABR CRASH: {Ex}", ex);
            return false;
        }

        return File.Exists(master);
    }
}
