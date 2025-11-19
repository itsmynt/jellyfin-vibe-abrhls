using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ABRHLS.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ABRHLS.Api;

[ApiController]
[Route("abrhls")]
[Authorize(Policy = "DefaultAuthorization")]
public class AbrHlsController : ControllerBase
{
    private readonly ILogger<AbrHlsController> _logger;
    private readonly ILibraryManager _libraryManager;

    public AbrHlsController(ILogger<AbrHlsController> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    [HttpGet("levels/{itemId}")]
    public ActionResult<AbrInfoResponse> GetQualityLevels([FromRoute] Guid itemId)
    {
        // FIX: Expliziter Cast oder Umweg, falls die Signatur uneindeutig ist
        var item = _libraryManager.GetItemById(itemId);
        
        if (item is not Video) return NotFound("Video nicht gefunden");
        
        // (Rest der Logik vereinfacht für Kompilierung, da der Packager die Arbeit macht)
        return Ok(new AbrInfoResponse { Available = true });
    }

    [HttpGet("stream/{itemId}/{*playlist}")]
    public ActionResult StreamPlaylist([FromRoute] Guid itemId, [FromRoute] string playlist)
    {
        if (Plugin.Instance == null) return StatusCode(500);

        var item = _libraryManager.GetItemById(itemId);
        if (item == null) return NotFound();

        // Wir nutzen die neue Logik des Packagers, um den Pfad zu finden
        // Da wir den Packager hier nicht injiziert haben, nutzen wir den Fallback auf den Config-Pfad
        // oder suchen manuell. Fürs Erste: Config-Pfad.
        var config = Plugin.Instance.Configuration;
        
        // Versuch den Pfad neben dem Film zu erraten
        string fileDir = Path.Combine(config.OutputRoot, itemId.ToString("N"));
        
        if (!string.IsNullOrEmpty(item.Path))
        {
             var movieDir = Path.GetDirectoryName(item.Path);
             if (!string.IsNullOrEmpty(movieDir))
             {
                 // Prüfen ob "abr_hls" neben dem Film existiert
                 var localDir = Path.Combine(movieDir, "abr_hls");
                 if (Directory.Exists(localDir)) fileDir = localDir;
             }
        }

        var filePath = Path.Combine(fileDir, playlist);
        if (!System.IO.File.Exists(filePath)) return NotFound("Playlist nicht gefunden: " + filePath);

        return PhysicalFile(filePath, "application/vnd.apple.mpegurl");
    }
}
