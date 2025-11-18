using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities; // Wichtig für die Typen

namespace Jellyfin.ABRHls.Services;

public class LibraryWatcher : BackgroundService
{
    private readonly ILogger<LibraryWatcher> _log;
    private readonly ILibraryManager _lib;
    private readonly HlsPackager _pack;
    private readonly Plugin _plugin;

    public LibraryWatcher(ILogger<LibraryWatcher> log, ILibraryManager lib, HlsPackager pack)
    {
        _log = log; _lib = lib; _pack = pack; _plugin = (Plugin)Plugin.Instance!;
        _lib.ItemAdded += OnItemAdded;
        // Updated Event entfernen wir erstmal zur Sicherheit, das verursacht oft Endlosschleifen
        // _lib.ItemUpdated += OnItemChanged; 
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Nur wenn Auto-Scan an ist UND es ein Video ist
        if (!_plugin.Configuration.AutoOnLibraryScan) return;

        if (e.Item is Video video && !video.IsVirtualItem)
        {
            _log.LogInformation("Neues Video entdeckt: {Title}. Starte ABR Generierung...", video.Name);
            // Task.Run damit der Library Scan nicht blockiert wird
            _ = Task.Run(() => _pack.EnsurePackedAsync(video.Id));
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}