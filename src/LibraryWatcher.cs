using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.ABRHls.Services;

public class LibraryWatcher : BackgroundService
{
    private readonly ILogger<LibraryWatcher> _log;
    private readonly ILibraryManager _lib;
    private readonly HlsPackager _pack;
    private readonly Plugin _plugin;

    public LibraryWatcher(ILogger<LibraryWatcher> log, ILibraryManager lib, HlsPackager pack)
    {
        _log = log; _lib = lib; _pack = pack; _plugin = Plugin.Instance!;
        _lib.ItemAdded += OnItemAdded;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video video && !video.IsVirtualItem)
        {
            _ = Task.Run(async () => 
            {
                try {
                    if (_plugin.Configuration.AutoOnLibraryScan) {
                        _log.LogWarning("ABR WATCHER: Neues Video entdeckt: {Name}", video.Name);
                        await _pack.EnsurePackedAsync(video.Id);
                    }
                } catch (Exception ex) { _log.LogError("ABR WATCHER FEHLER: {Ex}", ex); }
            });
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
