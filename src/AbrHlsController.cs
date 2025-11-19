using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.ABRHls.Models;
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

    [HttpGet("levels/{itemId}")]
    public ActionResult<AbrInfoResponse> GetQualityLevels([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video) return NotFound("Video nicht gefunden");

        var outputDir = _packager.GetOutputDir(item, "default");
        var levels = new List<QualityLevelInfo>();

        if (Directory.Exists(outputDir))
        {
            if (Plugin.Instance != null)
            {
                foreach (var profile in Plugin.Instance.Configuration.Ladder)
                {
                    levels.Add(new QualityLevelInfo { Label = profile.Label, Bitrate = profile.TargetBitrate });
                }
            }
        }

        return Ok(new AbrInfoResponse { Available = levels.Any(), Levels = levels });
    }

    [HttpGet("stream/{itemId}/{*playlist}")]
    public ActionResult StreamPlaylist([FromRoute] Guid itemId, [FromRoute] string playlist)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null) return NotFound();

        var outputDir = _packager.GetOutputDir(item, "default");
        var filePath = Path.Combine(outputDir, playlist);

        if (!System.IO.File.Exists(filePath)) return NotFound("Datei nicht gefunden: " + filePath);

        var contentType = playlist.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp4";
        return PhysicalFile(filePath, contentType);
    }
}

public class AbrInfoResponse
{
    public bool Available { get; set; }
    public List<QualityLevelInfo> Levels { get; set; } = new();
}

public class QualityLevelInfo
{
    public string Label { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bitrate { get; set; }
    public string Path { get; set; } = "";
}
