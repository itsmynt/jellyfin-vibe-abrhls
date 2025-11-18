using MediaBrowser.Model.Plugins;

namespace Jellyfin.ABRHls;

public class PluginConfiguration : BasePluginConfiguration
{
    // Leer lassen = Wir nutzen den FFmpeg Pfad von Jellyfin selbst
    public string FfmpegPath { get; set; } = "";

    public string OutputRoot { get; set; } = "data/abrhls";
    public int SegmentDurationSeconds { get; set; } = 4; // 4 Sekunden ist ein guter Standard für HLS
    public bool UseFmp4 { get; set; } = true;

    // WICHTIG: Standardmäßig AUS, damit der Server nicht explodiert
    public bool AutoOnLibraryScan { get; set; } = false;

    public List<LadderProfile> Ladder { get; set; } = new()
    {
        new("source", 0, 0, 12_000_000, 16_000_000, 32_000_000, "libx264", "aac", 192_000, useOriginalResolution:true),
        new("1080p", 1920, 1080, 6_000_000, 6_400_000, 12_000_000, "libx264", "aac", 128_000),
        new("720p", 1280, 720, 3_000_000, 3_200_000, 6_000_000, "libx264", "aac", 128_000),
        new("480p", 854, 480, 1_500_000, 1_800_000, 3_000_000, "libx264", "aac", 128_000) // 480p ist nützlicher als 540p
    };

    // ... (Rest der Listen kannst du so lassen oder bei Bedarf kürzen) ...
    // Ich habe hier der Übersicht halber gekürzt, der Rest der Datei kann bleiben wie er war, 
    // solange du die oberen Properties übernimmst.

    public List<LadderProfile> FireTvUhdSdr { get; set; } = new()
    {
        new("2160p", 3840, 2160, 12_000_000, 14_000_000, 28_000_000, "hevc", "eac3", 640_000),
        new("1080p", 1920, 1080, 5_000_000, 6_000_000, 12_000_000, "hevc", "eac3", 448_000),
        new("720p", 1280, 720, 2_500_000, 3_000_000, 6_000_000, "hevc", "eac3", 320_000),
        new("audio", 0, 0, 96_000, 128_000, 256_000, "", "aac", 96_000, useOriginalResolution:true)
    };

    public List<LadderProfile> FireTvUhdHdr { get; set; } = new()
    {
        new("2160p", 3840, 2160, 15_000_000, 18_000_000, 35_000_000, "hevc", "eac3", 640_000),
        new("1080p", 1920, 1080, 6_000_000, 7_000_000, 14_000_000, "hevc", "eac3", 448_000),
        new("audio", 0, 0, 96_000, 128_000, 256_000, "", "aac", 96_000, useOriginalResolution:true)
    };

    public bool EnableFireTvEndpoint { get; set; } = true;
    public bool KeepEac3IfPresent { get; set; } = true;
    public bool TranscodeDtsToAc3 { get; set; } = true;
    public bool TranscodeTrueHdToEac3 { get; set; } = true;
    public bool AddStereoAacFallback { get; set; } = true;
    public bool GenerateThumbnails { get; set; } = true;
    public int ThumbnailIntervalSeconds { get; set; } = 10;
    public int ThumbnailWidth { get; set; } = 240;
}