using System.Reflection;
using Android.Content;
using Android.Provider;

namespace TabloFireTv.Updates;

/// <summary>
/// The app's half of auto-update: ask <see cref="UpdateService"/> whether GitHub has a newer
/// release, and if the user agrees, hand the downloaded APK to the package installer.
/// </summary>
public static class AppUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly UpdateService Service = new(Http, UpdateSettings.Default);

    /// <summary>
    /// AppInfo reads the actual installed package's versionName (ApplicationDisplayVersion,
    /// e.g. "1.0") - the assembly version is a separate, unrelated .NET attribute that defaults
    /// to 1.0.0.0 and was never set here, so it would make every real release look newer.
    /// </summary>
    public static Version Current
    {
        get
        {
            string? text = null;
            try { text = AppInfo.Current.VersionString; }
            catch (Exception) { }

            return UpdateService.ParseVersion(text)
                ?? UpdateService.Normalise(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));
        }
    }

    public static Task<UpdateCheck> CheckAsync(CancellationToken cancel = default) =>
        Service.CheckAsync(Current, cancel);

    /// <summary>Downloads the APK to the app's cache and hands it to the installer.</summary>
    public static async Task<string?> DownloadAndInstallAsync(UpdateCheck check, CancellationToken cancel = default)
    {
        if (check.DownloadUrl is null || check.FileName is null) return "That release has no installer attached.";

        var folder = Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath, "updates");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, check.FileName);

        try
        {
            await Service.DownloadAsync(check.DownloadUrl, destination, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return $"The update could not be downloaded.\n\n{ex.Message}";
        }

        return Install(destination);
    }

    /// <summary>
    /// Hands the APK to the package installer as a content:// URI (a file:// URI has thrown
    /// FileUriExposedException since Nougat). Since Oreo the app also needs the user's "install
    /// unknown apps" permission for itself, granted once via the settings page this opens.
    /// </summary>
    private static string? Install(string apkPath)
    {
        var context = Android.App.Application.Context;

        if (OperatingSystem.IsAndroidVersionAtLeast(26) && !context.PackageManager!.CanRequestPackageInstalls())
        {
            var settings = new Intent(
                Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + context.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            return "Allow Tablo for Fire TV to install apps (using the remote), then select Update again.";
        }

        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
            context, context.PackageName + ".updates", new Java.IO.File(apkPath));

        var install = new Intent(Intent.ActionView);
        install.SetDataAndType(uri, "application/vnd.android.package-archive");
        install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(install);

        return null;
    }
}
