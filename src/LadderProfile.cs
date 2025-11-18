namespace Jellyfin.ABRHls; // Namespace angepasst an den Rest

public record LadderProfile(
    string Name, // Umbenannt von Label zu Name (passt besser zur Config)
    int Width,
    int Height,
    long MinBitrate,
    long TargetBitrate, // Wir nutzen dies als Haupt-Bitrate
    long MaxBitrate,
    string VideoCodec,
    string AudioCodec,
    long AudioBitrate,
    bool UseOriginalResolution = false,
    bool CopyVideo = false // Neu: Erlaubt "copy" (schnell) statt Transcode
)
{
    // Hilfseigenschaften für FFmpeg Kalkulationen
    public long Bitrate => TargetBitrate;
    public long Bufsize => MaxBitrate * 2;
}