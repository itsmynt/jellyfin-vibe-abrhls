using System.Xml.Serialization;

namespace Jellyfin.ABRHls;

public class LadderProfile
{
    // 1. Leerer Konstruktor (Pflicht für Jellyfin)
    public LadderProfile() {}

    // 2. Voller Konstruktor (Pflicht für unseren Code)
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

    // 3. Helper mit XmlIgnore (Verhindert Speicher-Fehler!)
    [XmlIgnore] public long Bitrate => TargetBitrate;
    [XmlIgnore] public long Maxrate => MaxBitrate;
    [XmlIgnore] public long Bufsize => MaxBitrate * 2;
}
