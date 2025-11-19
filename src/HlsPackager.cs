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

    // Hilfsmethode für Datei-Logging (Windows-Spezifisch)
    private void FileLog(string msg)
    {
        try { File.AppendAllText(@"C:\abrhls_debug.txt", $"{DateTime.Now}: {msg}\n"); } catch { }
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
        FileLog($"--- START ITEM {itemId} ---");

        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) 
        {
            FileLog("Item ist null oder hat keinen Pfad.");
            return false;
        }

        // 1. FFmpeg Pfad
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = @"E:\Program Files\Jellyfin\Server\ffmpeg.exe"; // Hardcoded Fallback für deinen Server

        FileLog($"FFmpeg Pfad: {ff}");
        FileLog($"Input Video: {item.Path}");

        // 2. Output Ordner
        var outDir = GetOutputDir(item.Id, profileName);
        try { 
            Directory.CreateDirectory(outDir); 
            FileLog($"Ordner erstellt: {outDir}");
        }
        catch (Exception ex) {
            FileLog($"FEHLER Ordner erstellen: {ex.Message}");
            return false;
        }

        // 3. Argumente bauen (Einfachster Test)
        // Wir nutzen Anführungszeichen für den Pfad, falls Leerzeichen drin sind
        var args = $"-y -i \"{item.Path}\" -c:v libx264 -preset ultrafast -f hls -hls_time 4 \"{Path.Combine(outDir, "master.m3u8")}\"";
        
        FileLog($"Argumente: {args}");

        var psi = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try 
        {
            FileLog("Starte Prozess...");
            using var p = Process.Start(psi);
            if (p == null)
            {
                FileLog("Prozess konnte nicht gestartet werden (null).");
                return false;
            }

            // Output lesen
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            
            FileLog($"Exit Code: {p.ExitCode}");
            if (p.ExitCode != 0)
            {
                FileLog($"FFMPEG FEHLER OUTPUT:\n{err}");
            }
            else
            {
                FileLog("Erfolg! Datei sollte da sein.");
            }
        }
        catch (Exception ex)
        {
            FileLog($"CRASH BEIM STARTEN: {ex}");
            return false;
        }

        return File.Exists(Path.Combine(outDir, "master.m3u8"));
    }
    
    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct) { }
}
