namespace Jellyfin.ABRHls;

// Wir nutzen hier 'record', das erstellt automatisch einen Konstruktor
public record LadderProfile(
    string Name, 
    int Width, 
    int Height, 
    long MinBitrate, 
    long TargetBitrate, 
    long MaxBitrate, 
    string VideoCodec, 
    string AudioCodec, 
    long AudioBitrate, 
    bool UseOriginalResolution = false, 
    bool CopyVideo = false
)
{
    // --- FIX: Helper-Eigenschaften für den Packager ---
    // Der Packager sucht nach "Bitrate", wir haben "TargetBitrate" -> wir leiten es um.
    public long Bitrate => TargetBitrate;
    
    // Der Packager sucht nach "Maxrate", wir haben "MaxBitrate".
    public long Maxrate => MaxBitrate; 
    
    // Buffergröße berechnen wir automatisch
    public long Bufsize => MaxBitrate * 2;
}
