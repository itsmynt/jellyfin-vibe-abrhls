using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ABRHls.Services;

public class HlsPackager
{
    private readonly ILogger<HlsPackager> _log;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly Plugin _plugin;

    public HlsPackager(ILogger<HlsPackager> log, IApplicationPaths paths, ILibraryManager library)
    { 
        _log = log; 
        _paths = paths; 
        _library = library; 
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
        // 1. Basis-Check
        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) 
        {
            _log.LogWarning("ABR: Item {Id} nicht gefunden.", itemId);
            return false;
        }

        // 2. FFmpeg Pfad
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg";

        _log.LogWarning("ABR DIAGNOSE: FFmpeg='{Path}' Video='{Video}'", ff, item.Path);

        // 3. Ordner erstellen
        var outDir = GetOutputDir(item.Id, profileName);
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            _log.LogError("ABR: Ordner-Fehler {Dir}: {Ex}", outDir, ex.Message);
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true;

        // 4. Argumente bauen (String-basiert für Windows-Sicherheit)
        // Anführungszeichen im Pfad escapen
        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        var varMap = new List<string>();

        // Nur das erste Profil für den Test nutzen
        if (ladder.Count > 0) 
        {
            var L = ladder[0];
            args += $" -map 0:v:0 -c:v:0 libx264 -b:v:0 {L.Bitrate} -preset ultrafast";
            args += " -map 0:a:0? -c:a:0 aac -b:a:0 128k";
            varMap.Add($"v:0,a:0,name:{L.Name}");
        }

        // --- HIER WAR DER FEHLER: Wir teilen die Zeile auf ---
        args += " -master_pl_name master.m3u8";
        args += " -var_stream_map \"" + string.Join(" ", varMap) + "\"";
        args += " -f hls -hls_time 4 -hls_playlist_type vod";
        
        // Pfade für Segmente und Playlists bauen
        string segPath = Path.Combine(outDir, "%v", "seg_%03d.ts");
        string indexPath = Path.Combine(outDir, "%v", "index.m3u8");
        
        args += " -hls_segment_filename \"" + segPath + "\"";
        args += " \"" + indexPath + "\"";
        // -----------------------------------------------------

        // Unterordner für die Streams erstellen (z.B. "1080p")
        foreach(var m in varMap)
        {
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null) 
             {
                 string subDir = Path.Combine(outDir, namePart.Substring(5));
                 Directory.CreateDirectory(subDir);
             }
        }

        _log.LogWarning("ABR CMD: {Bin} {Args}", ff, args);

        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = outDir 
        };

        try 
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                // Fehlerkanal lesen (FFmpeg schreibt Logs nach Stderr)
                var errTask = p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                
                var errOutput = await errTask;

                if (p.ExitCode != 0) 
                {
                    _log.LogError("ABR FEHLER: ExitCode {Code}. Output:\n{Err}", p.ExitCode, errOutput);
                    return false;
                }
                
                _log.LogWarning("ABR ERFOLG: Fertig!");
            }
        }
        catch (Exception ex)
        {
            _log.LogError("ABR EXCEPTION: {Ex}", ex.Message);
            return false;
        }

        return File.Exists(master);
    }
    
    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct) { }
}
