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

    // Konstante für die Log-Datei
    private const string LogFile = @"E:\abrhls_debug.txt";

    public HlsPackager(ILogger<HlsPackager> log, IApplicationPaths paths, ILibraryManager library)
    { 
        _log = log; 
        _paths = paths; 
        _library = library; 
        _plugin = Plugin.Instance!; 
        
        // Start-Eintrag
        FileLog(">>> PLUGIN WURDE INITIALISIERT <<<");
    }

    // Hilfsmethode für Datei-Logging
    private void FileLog(string msg)
    {
        try 
        { 
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} | {msg}\n"); 
        } 
        catch 
        { 
            // Wenn wir nicht schreiben können, versuchen wir es im System-Log
            _log.LogError("ABR FILE LOG ERROR: {Msg}", msg);
        }
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
        FileLog($"--- START AUFTRAG: Item {itemId} ---");

        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) 
        {
            FileLog("FEHLER: Item ist null oder hat keinen Pfad.");
            return false;
        }

        // 1. FFmpeg Pfad
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        // Fallback Hardcoded für deinen Server
        if (string.IsNullOrWhiteSpace(ff)) ff = @"E:\Program Files\Jellyfin\Server\ffmpeg.exe";

        FileLog($"FFmpeg Pfad: {ff}");
        FileLog($"Video Input: {item.Path}");

        // 2. Output Ordner
        var outDir = GetOutputDir(item.Id, profileName);
        FileLog($"Output Ziel: {outDir}");
        
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) {
            FileLog($"KRITISCH: Kann Ordner nicht erstellen! {ex.Message}");
            return false;
        }

        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) 
        {
            FileLog("Datei existiert bereits. Überspringe.");
            return true;
        }

        // 3. Einfachster FFmpeg Befehl zum Testen
        // Escaping für Windows Pfade
        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        
        // Ein einfaches Profil hinzufügen
        if (ladder.Count > 0) 
        {
            var L = ladder[0];
            args += $" -map 0:v:0 -c:v:0 libx264 -b:v:0 {L.Bitrate} -preset ultrafast";
            args += " -map 0:a:0? -c:a:0 aac -b:a:0 128k";
        }

        // HLS Parameter
        args += " -f hls -hls_time 4 -hls_playlist_type vod";
        args += " -master_pl_name master.m3u8";
        args += " -hls_segment_filename \"" + Path.Combine(outDir, "seg_%03d.ts") + "\"";
        args += " \"" + Path.Combine(outDir, "index.m3u8") + "\"";

        FileLog($"Starte Befehl: {ff} {args}");

        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = outDir 
        };

        try 
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                FileLog("FEHLER: Process.Start hat NULL zurückgegeben!");
                return false;
            }

            // Output lesen
            string err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            
            FileLog($"Prozess beendet. Exit Code: {p.ExitCode}");
            if (p.ExitCode != 0)
            {
                FileLog($"FFMPEG FEHLER LOG:\n{err}");
            }
            else
            {
                FileLog("ERFOLG! Konvertierung abgeschlossen.");
            }
        }
        catch (Exception ex)
        {
            FileLog($"CRASH Exception: {ex}");
            return false;
        }

        return File.Exists(master);
    }
    
    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct) { }
}
