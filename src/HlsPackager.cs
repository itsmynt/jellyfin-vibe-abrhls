using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ABRHls.Services;

public class HlsPackager
{
    private readonly ILogger<HlsPackager> _log;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly Plugin _plugin;

    public HlsPackager(ILogger<HlsPackager> log, IApplicationPaths paths, ILibraryManager library)
    { 
        _log = log; 
        _paths = paths; 
        _library = library; 
        _plugin = Plugin.Instance!; // Das funktioniert jetzt, weil wir Instance in Plugin.cs hinzugefügt haben
    }

    public string GetOutputDir(Guid itemId, string profile = "default")
    {
        var cfg = _plugin.Configuration;
        var root = Path.IsPathRooted(cfg.OutputRoot) ? cfg.OutputRoot : Path.Combine(_paths.DataPath, cfg.OutputRoot);
        return Path.Combine(root, itemId.ToString(), profile);
    }

    public Task<bool> EnsurePackedAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.Ladder, "default", ct, fireTv:false, hdr:false);

    public Task<bool> EnsurePackedFireTvSdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.FireTvUhdSdr, "firetv_sdr", ct, fireTv:true, hdr:false);

    public Task<bool> EnsurePackedFireTvHdrAsync(Guid itemId, CancellationToken ct = default)
        => EnsurePackedAsync(itemId, _plugin.Configuration.FireTvUhdHdr, "firetv_hdr", ct, fireTv:true, hdr:true);

    private async Task<bool> EnsurePackedAsync(Guid itemId, List<LadderProfile> ladder, string profileName, CancellationToken ct, bool fireTv, bool hdr)
    {
        var item = _library.GetItemById(itemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) return false;

        var outDir = GetOutputDir(item.Id, profileName);
        Directory.CreateDirectory(outDir);
        var master = Path.Combine(outDir, "master.m3u8");
        if (File.Exists(master)) return true;

        var cfg = _plugin.Configuration;
        var ff = cfg.FfmpegPath;
        var seg = Math.Clamp(cfg.SegmentDurationSeconds, 2, 6);

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", Quote(item.Path!) };
        var varMap = new List<string>();

        string? srcAcodec = null; int? srcChannels = null; bool srcIsEac3Atmos = false;
        try
        {
            // FIX: Wir nutzen GetMediaStreams() statt GetMediaStream()
            var audioStream = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            if (audioStream != null)
            {
                srcAcodec = audioStream.Codec?.ToLowerInvariant();
                srcChannels = audioStream.Channels;
                srcIsEac3Atmos = srcAcodec == "eac3";
            }
        }
        catch {}

        int aindex = 0;

        for (int i = 0; i < ladder.Count; i++)
        {
            var L = ladder[i];

            if (L.Name == "audio")
            {
                if (!cfg.AddStereoAacFallback) continue;
                args.AddRange(new[]{ "-map", "0:a:0?", $"-c:a:{aindex}", "aac", $"-b:a:{aindex}", Math.Max(96_000, L.AudioBitrate).ToString(), $"-vn:{aindex}", "1" });
                varMap.Add($"a:{aindex},name:{L.Name}");
                aindex++;
                continue;
            }

            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });

            if (L.CopyVideo) { args.AddRange(new[] { $"-c:v:{i}", "copy" }); }
            else
            {
                var vcodec = L.VideoCodec;
                if (hdr && vcodec == "hevc")
                {
                    args.AddRange(new[] { $"-c:v:{i}", "hevc", $"-profile:v:{i}", "main10", $"-pix_fmt:{i}", "yuv420p10le" });
                }
                else
                {
                    args.AddRange(new[] { $"-c:v:{i}", vcodec, $"-pix_fmt:{i}", "yuv420p" });
                }
                args.AddRange(new[] { $"-b:v:{i}", L.Bitrate.ToString(), $"-maxrate:v:{i}", L.Maxrate.ToString(), $"-bufsize:v:{i}", L.Bufsize.ToString(),
                                      $"-preset:{i}", "veryfast", $"-g:{i}", (seg*12).ToString(), $"-keyint_min:{i}", (seg*12).ToString(), $"-sc_threshold:{i}", "0" });

                if (!L.UseOriginalResolution && L.Width > 0 && L.Height > 0)
                    args.AddRange(new[] { $"-vf:{i}", $"scale=w={L.Width}:h={L.Height}:force_original_aspect_ratio=decrease" });
            }

            if (srcAcodec == "eac3" && cfg.KeepEac3IfPresent)
            {
                args.AddRange(new[] { $"-c:a:{i}", "copy" });
            }
            else if ((srcAcodec == "dts" || srcAcodec == "dca") && cfg.TranscodeDtsToAc3)
            {
                args.AddRange(new[] { $"-c:a:{i}", "ac3", $"-b:a:{i}", (srcChannels >= 6 ? 448_000 : 192_000).ToString() });
            }
            else if ((srcAcodec == "truehd" || srcAcodec == "mlp") && cfg.TranscodeTrueHdToEac3)
            {
                args.AddRange(new[] { $"-c:a:{i}", "eac3", $"-b:a:{i}", "640k" });
            }
            else
            {
                if (srcChannels >= 6)
                    args.AddRange(new[] { $"-c:a:{i}", "eac3", $"-b:a:{i}", "448k" });
                else
                    args.AddRange(new[] { $"-c:a:{i}", "aac", $"-b:a:{i}", "128k", $"-ac:{i}", "2" });
            }

            varMap.Add($"v:{i},a:{i},name:{L.Name}");
        }

        var flags = cfg.UseFmp4 ? "independent_segments+iframes_only+split_by_time" : "independent_segments";
        var segType = cfg.UseFmp4 ? "-hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4" : string.Empty;

        args.AddRange(new[] {
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", Quote(string.Join(' ', varMap)),
            "-hls_playlist_type", "vod",
            "-hls_time", seg.ToString(),
            "-hls_flags", flags,
            "-f", "hls",
            "-hls_segment_filename", Quote(Path.Combine(outDir, "%v/seg_%06d.m4s")),
            Quote(Path.Combine(outDir, "%v/index.m3u8"))
        });
        if (!string.IsNullOrWhiteSpace(segType))
        {
            var insertAt = args.IndexOf("-f");
            args.InsertRange(insertAt, segType.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        Directory.CreateDirectory(Path.Combine(outDir, "%v"));

        var psi = new ProcessStartInfo(ff, string.Join(' ', args)) { RedirectStandardError = true, RedirectStandardOutput = true };
        _log.LogInformation("FFmpeg HLS packager: {Args}", psi.Arguments);
        using var p = Process.Start(psi);
        if (p == null) return false;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            _log.LogError("FFmpeg failed with code {Code}. StdErr: {Err}", p.ExitCode, await p.StandardError.ReadToEndAsync(ct));
            return false;
        }

        if (cfg.GenerateThumbnails)
            await GenerateWebVttThumbnailsAsync(ff, item, outDir, cfg.ThumbnailIntervalSeconds, cfg.ThumbnailWidth, ct);

        return File.Exists(master);
    }

    private async Task GenerateWebVttThumbnailsAsync(string ffmpeg, Video item, string outDir, int interval, int width, CancellationToken ct)
    {
        var thumbsDir = Path.Combine(outDir, "thumbs");
        Directory.CreateDirectory(thumbsDir);
        var pattern = Path.Combine(thumbsDir, "thumb_%05d.jpg");
        var args = $"-y -hide_banner -loglevel error -i {Quote(item.Path!)} -vf fps=1/{interval},scale={width}:-1 -q:v 7 {Quote(pattern)}";
        using (var p = Process.Start(new ProcessStartInfo(ffmpeg, args){ RedirectStandardError=true, RedirectStandardOutput=true }))
        { if (p != null) await p.WaitForExitAsync(ct); }

        var files = Directory.GetFiles(thumbsDir, "thumb_*.jpg").OrderBy(f => f).ToArray();
        var vtt = Path.Combine(outDir, "thumbnails.vtt");
        using var sw = new StreamWriter(vtt);
        sw.WriteLine("WEBVTT");
        for (int i = 0; i < files.Length; i++)
        {
            var start = TimeSpan.FromSeconds(i * interval);
            var end = TimeSpan.FromSeconds((i + 1) * interval);
            sw.WriteLine($"{FormatTs(start)} --> {FormatTs(end)}");
            sw.WriteLine($"thumbs/{Path.GetFileName(files[i])}");
            sw.WriteLine();
        }
    }

    private static string FormatTs(TimeSpan t) => $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.000";
    private static string Quote(string s) => $"\"{s}\"";
}
