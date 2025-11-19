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

    // WICHTIG: IMediaEncoder entfernt, um Startprobleme zu vermeiden
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
        _log.LogWarning("ABR PACKAGER: Starte für Item {Id}", itemId); // Lebenszeichen

        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) 
        {
            _log.LogWarning("ABR: Item nicht gefunden.");
            return false;
        }

        // FFmpeg Pfad ermitteln (Simpel)
        var cfg = _plugin.Configuration;
        string ff = cfg.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ff)) ff = "ffmpeg"; // Standard-Befehl

        var outDir = GetOutputDir(item.Id, profileName);
        Directory.CreateDirectory(outDir);

        // Dateipfad sicher quoten
        var inputPath = item.Path.Replace("\"", "\\\"");
        var args = $"-y -hide_banner -loglevel error -i \"{inputPath}\"";
        
        // ... (Hier würde der restliche komplexe Code kommen, aber für den Test reicht ein einfacher Befehl)
        // Wir machen einen "Dummy" Test, um zu sehen ob FFmpeg überhaupt startet
        
        // ECHTER CODE START (gekürzt für Stabilität)
        var varMap = new List<string>();
        int aindex = 0;
        // Wir nehmen nur das erste Profil zum Testen, um Komplexität zu reduzieren
        if(ladder.Count > 0) {
            var L = ladder[0];
            args += $" -map 0:v:0 -c:v:0 libx264 -b:v:0 {L.Bitrate} -preset ultrafast";
            args += $" -map 0:a:0? -c:a:0 aac -b:a:0 128k";
            varMap.Add($"v:0,a:0,name:{L.Name}");
        }

        args += $" -master_pl_name master.m3u8 -var_stream_map \"{string.Join(" ", varMap)}\" -f hls -hls_time 4 -hls_playlist_type vod -hls_segment_filename \"{Path.Combine(outDir, "%v/seg_%03d.ts")}\" \"{Path.Combine(outDir, "%v/index.m3u8")}\"";

        // Unterordner erstellen
        foreach(var m in varMap)
        {
             var parts = m.Split(',');
             var namePart = parts.FirstOrDefault(p => p.StartsWith("name:"));
             if(namePart != null) Directory.CreateDirectory(Path.Combine(outDir, namePart.Substring(5)));
        }

        _log.LogWarning("ABR CMD: {Bin
