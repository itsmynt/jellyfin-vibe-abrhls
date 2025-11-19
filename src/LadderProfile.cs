namespace Jellyfin.ABRHls;

public class LadderProfile
{
    public string Label { get; set; } = "1080p";
    public int Width { get; set; }
    public int Height { get; set; }
    public long MinBitrate { get; set; }
    public long TargetBitrate { get; set; }
    public long MaxBitrate { get; set; }
    public string VideoCodec { get; set; } = "libx264";
    public string AudioCodec { get; set; } = "aac";
    public long AudioBitrate { get; set; } = 128000;
    public bool UseOriginalResolution { get; set; } = false;
    public bool CopyVideo { get; set; } = false;
    public int Bufsize { get; set; } = 0;
    public int Maxrate { get; set; } = 0;

    public LadderProfile() { }

    public LadderProfile(string label, int w, int h, long min, long target, long max, string vcodec, string acodec, long abit, bool useOriginalResolution = false)
    {
        Label = label; Width = w; Height = h; 
        MinBitrate = min; TargetBitrate = target; MaxBitrate = max;
        VideoCodec = vcodec; AudioCodec = acodec; AudioBitrate = abit; 
        UseOriginalResolution = useOriginalResolution;
        Bufsize = (int)max * 2;
        Maxrate = (int)max;
    }
}
