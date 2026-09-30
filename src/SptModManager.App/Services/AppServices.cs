using SptModManager.Core;
using SptModManager.Core.Forge;
using SptModManager.Core.Net;
using SptModManager.Core.Spt;

namespace SptModManager.App.Services;

/// <summary>The app's long-lived services, created once at startup.</summary>
public sealed class AppServices
{
    public AppServices(AppSettings settings, IDialogService dialogs)
    {
        Settings = settings;
        Dialogs = dialogs;
        Http = HttpClientFactory.CreateApiClient();
        Downloader = new FileDownloader(HttpClientFactory.CreateDownloadClient());
        Log = new UiActivityLog();
        Images = new ImageLoader(Http, Log, () => Settings.ForgeBaseUrl);
        Forge = new ForgeClient(Http, settings.ForgeBaseUrl);
        Releases = new SptReleaseClient(Http, settings.ReleaseRepository);
    }

    /// <summary>Test/design-time constructor with injectable clients.</summary>
    public AppServices(AppSettings settings, IDialogService dialogs, IForgeClient forge, ISptReleaseClient releases, IFileDownloader downloader, HttpClient http)
    {
        Settings = settings;
        Dialogs = dialogs;
        Http = http;
        Downloader = downloader;
        Log = new UiActivityLog();
        Images = new ImageLoader(http, Log, () => Settings.ForgeBaseUrl);
        Forge = forge;
        Releases = releases;
    }

    public AppSettings Settings { get; }

    public IDialogService Dialogs { get; }

    public HttpClient Http { get; }

    public IFileDownloader Downloader { get; }

    public ImageLoader Images { get; }

    public UiActivityLog Log { get; }

    public IForgeClient Forge { get; private set; }

    public ISptReleaseClient Releases { get; private set; }

    /// <summary>Rebuilds API clients after the endpoints change in settings.</summary>
    public void ApplyEndpointSettings()
    {
        if (Forge is ForgeClient)
        {
            Forge = new ForgeClient(Http, Settings.ForgeBaseUrl);
        }

        if (Releases is SptReleaseClient)
        {
            Releases = new SptReleaseClient(Http, Settings.ReleaseRepository);
        }
    }
}
