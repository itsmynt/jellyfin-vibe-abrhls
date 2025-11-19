using MediaBrowser.Model.Plugins;

namespace Jellyfin.ABRHls;

public class PluginConfiguration : BasePluginConfiguration
{
    public string FfmpegPath { get; set; } = "";
    public string OutputRoot { get; set; } = "data/abrhls";
    public int SegmentDurationSeconds { get; set; } = 4;
    public bool UseFmp4 { get; set; } = true;
    public bool AutoOnLibraryScan { get; set; } = true;

    public List<LadderProfile> Ladder { get; set; } = new()
    {
        new("source", 0, 0, 12_000_000, 16_000_000, 32_000_000, "libx264", "aac", 192_000, useOriginalResolution:true),
        new("1080p", 1920, 1080, 6_000_000, 6_400_000, 12_000_000, "libx264", "aac", 128_000),
        new("720p", 1280, 720, 3_000_000, 3_200_000, 6_000_000, "libx264", "aac", 128_000),
        new("480p", 848, 480, 1_500_000, 2_000_000, 4_000_000, "libx264", "aac", 128_000)
    };

    public List<LadderProfile> FireTvUhdSdr { get; set; } = new();
    public List<LadderProfile> FireTvUhdHdr { get; set; } = new();
    
    public bool KeepEac3IfPresent { get; set; } = true;
    public bool AddStereoAacFallback { get; set; } = true;
    public bool GenerateThumbnails { get; set; } = true;
    public int ThumbnailIntervalSeconds { get; set; } = 10;
    public int ThumbnailWidth { get; set; } = 240;
}
