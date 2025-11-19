namespace Jellyfin.ABRHls;

public class LadderProfile
{
    // 1. Der leere Konstruktor (WICHTIG für Jellyfin/XML!)
    public LadderProfile() 
    {
    }

    // 2. Der Konstruktor zum einfachen Erstellen im Code
    public LadderProfile(string name, int width, int height, long minBitrate, long targetBitrate, long maxBitrate, string videoCodec, string audioCodec, long audioBitrate, bool useOriginalResolution = false, bool copyVideo = false)
    {
        Name = name;
        Width = width;
        Height = height;
        MinBitrate = minBitrate;
        TargetBitrate = targetBitrate;
        MaxBitrate = maxBitrate;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
        AudioBitrate = audioBitrate;
        UseOriginalResolution = useOriginalResolution;
        CopyVideo = copyVideo;
    }

    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long MinBitrate { get; set; }
    public long TargetBitrate { get; set; }
    public long MaxBitrate { get; set; }
    public string VideoCodec { get; set; } = "libx264";
    public string AudioCodec { get; set; } = "aac";
    public long AudioBitrate { get; set; }
    public bool UseOriginalResolution { get; set; }
    public bool CopyVideo { get; set; }

    // Helper-Eigenschaften für den Packager (Read-only)
    public long Bitrate => TargetBitrate;
    public long Maxrate => MaxBitrate;
    public long Bufsize => MaxBitrate * 2;
}
