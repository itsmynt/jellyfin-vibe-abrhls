using System.Net.Mime;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.ABRHls.Services;

namespace Jellyfin.ABRHls.Api;

/// <summary>
/// Dieser Controller serviert die statischen HLS-Dateien an den Browser.
/// Er fungiert als Brücke zwischen Dateisystem und Web-Player.
/// </summary>
[ApiController]
[Route("AbrHls")] // Basis-URL: /AbrHls/...
[Authorize] // Nur eingeloggte User dürfen streamen
public class AbrHlsController : ControllerBase
{
    private readonly ILogger<AbrHlsController> _logger;
    private readonly HlsPackager _packager;
    private readonly ILibraryManager _libraryManager;

    public AbrHlsController(
        ILogger<AbrHlsController> logger,
        HlsPackager packager,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _packager = packager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Endpoint 1: Liefert die Master-Playlist (Das "Inhaltsverzeichnis")
    /// URL: /AbrHls/manifest/{itemId}
    /// </summary>
    [HttpGet("manifest/{itemId}")]
    [HttpGet("manifest/firetv/sdr/{itemId}")] // Alias für FireTV SDR
    [HttpGet("manifest/firetv/hdr/{itemId}")] // Alias für FireTV HDR
    public async Task<ActionResult> GetMasterManifest([FromRoute] Guid itemId)
    {
        // Ermittle den Modus anhand der URL
        var path = Request.Path.Value?.ToLower() ?? "";
        string profile = "default";
        bool fireTv = false, hdr = false;

        if (path.Contains("/firetv/sdr/")) { profile = "firetv_sdr"; fireTv = true; }
        else if (path.Contains("/firetv/hdr/")) { profile = "firetv_hdr"; fireTv = true; hdr = true; }

        // 1. Prüfen ob Dateien existieren, sonst Job starten
        bool ready = false;
        if (fireTv && hdr) ready = await _packager.EnsurePackedFireTvHdrAsync(itemId);
        else if (fireTv) ready = await _packager.EnsurePackedFireTvSdrAsync(itemId);
        else ready = await _packager.EnsurePackedAsync(itemId);

        if (!ready)
        {
            // Wenn FFmpeg noch arbeitet oder fehlgeschlagen ist
            return NotFound("Stream wird noch generiert oder Fehler aufgetreten. Bitte kurz warten.");
        }

        // 2. Pfad zur master.m3u8 holen
        var outDir = _packager.GetOutputDir(itemId, profile);
        var masterPath = Path.Combine(outDir, "master.m3u8");

        if (!System.IO.File.Exists(masterPath)) return NotFound("Manifest nicht gefunden.");

        // 3. Datei ausliefern
        return PhysicalFile(masterPath, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Endpoint 2: Liefert die eigentlichen Video-Schnipsel (Segments) und Unter-Playlists
    /// URL: /AbrHls/manifest/{itemId}/{filename}  oder .../{subdir}/{filename}
    /// Der Parameter {*suffix} fängt alles nach der ID ab (auch Unterordner).
    /// </summary>
    [HttpGet("manifest/{itemId}/{*suffix}")]
    [HttpGet("manifest/firetv/sdr/{itemId}/{*suffix}")]
    [HttpGet("manifest/firetv/hdr/{itemId}/{*suffix}")]
    public ActionResult GetStreamFile([FromRoute] Guid itemId, [FromRoute] string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return BadRequest();

        // Profil wiedererkennen
        var pathUrl = Request.Path.Value?.ToLower() ?? "";
        string profile = "default";
        if (pathUrl.Contains("/firetv/sdr/")) profile = "firetv_sdr";
        else if (pathUrl.Contains("/firetv/hdr/")) profile = "firetv_hdr";

        // Basis-Ordner
        var outDir = _packager.GetOutputDir(itemId, profile);

        // Sicherer Pfad-Zusammenbau (Verhindert "../" Attacken)
        var fullPath = Path.GetFullPath(Path.Combine(outDir, suffix));

        // Sicherheitscheck: Darf nicht aus dem outDir ausbrechen
        if (!fullPath.StartsWith(Path.GetFullPath(outDir))) return Forbid();

        if (!System.IO.File.Exists(fullPath)) return NotFound();

        // Content-Type raten
        string contentType = "application/octet-stream";
        if (fullPath.EndsWith(".m3u8")) contentType = "application/vnd.apple.mpegurl";
        else if (fullPath.EndsWith(".ts")) contentType = "video/mp2t";
        else if (fullPath.EndsWith(".m4s")) contentType = "video/iso.segment";
        else if (fullPath.EndsWith(".mp4")) contentType = "video/mp4";
        else if (fullPath.EndsWith(".vtt")) contentType = "text/vtt";
        else if (fullPath.EndsWith(".jpg")) contentType = "image/jpeg";

        return PhysicalFile(fullPath, contentType);
    }
}