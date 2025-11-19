using System.Net.Mime;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.ABRHls.Services;
// Wichtig: Namespace Jellyfin.ABRHls nutzen, damit LadderProfile gefunden wird
using Jellyfin.ABRHls; 

namespace Jellyfin.ABRHls.Api;

[ApiController]
[Route("AbrHls")]
[Authorize]
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

    [HttpGet("manifest/{itemId}")]
    [HttpGet("manifest/firetv/sdr/{itemId}")]
    [HttpGet("manifest/firetv/hdr/{itemId}")]
    public async Task<ActionResult> GetMasterManifest([FromRoute] Guid itemId)
    {
        var path = Request.Path.Value?.ToLower() ?? "";
        string profile = "default";
        bool fireTv = false, hdr = false;

        if (path.Contains("/firetv/sdr/")) { profile = "firetv_sdr"; fireTv = true; }
        else if (path.Contains("/firetv/hdr/")) { profile = "firetv_hdr"; fireTv = true; hdr = true; }

        // Check ob Dateien da sind
        bool ready = false;
        if (fireTv && hdr) ready = await _packager.EnsurePackedFireTvHdrAsync(itemId);
        else if (fireTv) ready = await _packager.EnsurePackedFireTvSdrAsync(itemId);
        else ready = await _packager.EnsurePackedAsync(itemId);

        if (!ready) return NotFound("Stream wird generiert... Bitte warten.");

        var outDir = _packager.GetOutputDir(itemId, profile);
        var masterPath = Path.Combine(outDir, "master.m3u8");

        if (!System.IO.File.Exists(masterPath)) return NotFound("Manifest nicht gefunden.");

        return PhysicalFile(masterPath, "application/vnd.apple.mpegurl");
    }

    [HttpGet("manifest/{itemId}/{*suffix}")]
    [HttpGet("manifest/firetv/sdr/{itemId}/{*suffix}")]
    [HttpGet("manifest/firetv/hdr/{itemId}/{*suffix}")]
    public ActionResult GetStreamFile([FromRoute] Guid itemId, [FromRoute] string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return BadRequest();

        var pathUrl = Request.Path.Value?.ToLower() ?? "";
        string profile = "default";
        if (pathUrl.Contains("/firetv/sdr/")) profile = "firetv_sdr";
        else if (pathUrl.Contains("/firetv/hdr/")) profile = "firetv_hdr";

        var outDir = _packager.GetOutputDir(itemId, profile);
        var fullPath = Path.GetFullPath(Path.Combine(outDir, suffix));
        
        // Sicherheitscheck (Path Traversal verhindern)
        if (!fullPath.StartsWith(Path.GetFullPath(outDir))) return Forbid();

        if (!System.IO.File.Exists(fullPath)) return NotFound();

        string contentType = "application/octet-stream";
        if (fullPath.EndsWith(".m3u8")) contentType = "application/vnd.apple.mpegurl";
        else if (fullPath.EndsWith(".ts")) contentType = "video/mp2t";
        else if (fullPath.EndsWith(".m4s")) contentType = "video/iso.segment";
        else if (fullPath.EndsWith(".mp4")) contentType = "video/mp4";
        else if (fullPath.EndsWith(".vtt")) contentType = "text/vtt";
        else if (fullPath.EndsWith(".jpg")) contentType = "image/jpeg";

        return PhysicalFile(fullPath, contentType);
    }

    // Endpoint für die Config-Seite / API Check
    [HttpGet("levels/{itemId}")]
    public ActionResult GetLevels([FromRoute] Guid itemId)
    {
        if (Plugin.Instance == null) return StatusCode(500);
        var config = Plugin.Instance.Configuration;
        
        var outDir = _packager.GetOutputDir(itemId);
        var levels = new List<object>();

        if (Directory.Exists(outDir))
        {
            foreach (var p in config.Ladder)
            {
                // KORREKTUR: Hier muss p.Name stehen (nicht p.Label)
                if (System.IO.File.Exists(Path.Combine(outDir, $"{p.Name}.m3u8"))) 
                {
                    levels.Add(new { p.Name, p.Bitrate, p.Width });
                }
            }
        }
        return Ok(levels);
    }
}
