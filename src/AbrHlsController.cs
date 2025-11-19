using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.ABRHls;
using Jellyfin.ABRHls.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ABRHls.Api;

[ApiController]
[Route("abrhls")]
[Authorize(Policy = "DefaultAuthorization")]
public class AbrHlsController : ControllerBase
{
    private readonly ILogger<AbrHlsController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly HlsPackager _packager;

    public AbrHlsController(ILogger<AbrHlsController> logger, ILibraryManager libraryManager, HlsPackager packager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _packager = packager;
    }

    [HttpGet("stream/{itemId}/{*playlist}")]
    public ActionResult StreamPlaylist([FromRoute] Guid itemId, [FromRoute] string playlist)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null) return NotFound();

        // Packager nutzen, um den korrekten Ordner zu finden (egal ob Film-Ordner oder Fallback)
        var outputDir = _packager.GetOutputDir(item, "default");
        var filePath = Path.Combine(outputDir, playlist);

        if (!System.IO.File.Exists(filePath)) return NotFound("Datei nicht gefunden: " + filePath);

        var contentType = playlist.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp4";
        return PhysicalFile(filePath, contentType);
    }
}
