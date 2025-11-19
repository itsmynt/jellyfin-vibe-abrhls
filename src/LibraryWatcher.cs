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
        _log = log; _lib = lib; _pack = pack; 
        _plugin = Plugin.Instance!; // Nutzung der statischen Instanz
        
        _lib.ItemAdded += OnItemAdded;
        // Optional: Auch bei Updates reagieren (kann aber Endlosschleifen erzeugen)
        // _lib.ItemUpdated += OnItemUpdated; 
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // DIAGNOSE: Zeige IMMER, dass ein Item gefunden wurde
        if (e.Item is Video video && !video.IsVirtualItem)
        {
            bool auto = _plugin.Configuration.AutoOnLibraryScan;
            _log.LogWarning("ABR WATCHER: Neues Video '{Name}' (ID: {Id}). AutoScan ist: {Status}", video.Name, video.Id, auto);

            if (auto)
            {
                _ = Task.Run(() => _pack.EnsurePackedAsync(video.Id));
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
