using System.Text.Json;
using Microsoft.Extensions.Logging;
using TabloFireTv.Models;

namespace TabloFireTv;

/// <summary>Tablo account credentials, and which of the account's DVRs to use.</summary>
public sealed record Credentials(string Email, string Password, string? ServerId);

/// <summary>
/// Remembers the Tablo sign-in on the device, so opening the app does not mean typing an email
/// and password with a remote every time. Kept in Android's encrypted storage (keys held by the
/// Android Keystore), and removed again by signing out.
/// </summary>
public static class CredentialStore
{
    private const string Key = "tablo.credentials.v1";

    public static bool HasSaved => Preferences.Default.ContainsKey(Key + ".present");

    public static void Save(Credentials credentials, ILogger log)
    {
        try
        {
            SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(credentials)).GetAwaiter().GetResult();
            Preferences.Default.Set(Key + ".present", true);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not save the sign-in: {Message}", ex.Message);
        }
    }

    public static Credentials? Load(ILogger log)
    {
        if (!HasSaved) return null;
        try
        {
            var json = SecureStorage.Default.GetAsync(Key).GetAwaiter().GetResult();
            return json is null ? null : JsonSerializer.Deserialize<Credentials>(json);
        }
        catch (Exception ex)
        {
            log.LogWarning("The saved sign-in could not be read ({Message}); sign in again", ex.Message);
            return null;
        }
    }

    public static void Clear()
    {
        SecureStorage.Default.Remove(Key);
        Preferences.Default.Remove(Key + ".present");
    }
}

/// <summary>
/// The last complete guide, on disk. A full guide load is hundreds of calls - the hardest thing
/// anything asks of the DVR - and an app on a TV stick is started and killed far more often than
/// a server is restarted, so without this every launch would reload it from scratch.
/// </summary>
public static class GuideStore
{
    private sealed record Saved(string ServerId, DateTime SavedUtc, List<GuideAiring> Device, List<GuideAiring> Fast);

    private static string File => Path.Combine(FileSystem.AppDataDirectory, "guide.json");

    public static void Save(string serverId, List<GuideAiring> device, List<GuideAiring> fast, ILogger log)
    {
        try
        {
            var tmp = File + ".tmp";
            using (var stream = System.IO.File.Create(tmp))
                JsonSerializer.Serialize(stream, new Saved(serverId, DateTime.UtcNow, device, fast));
            System.IO.File.Move(tmp, File, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not save the guide: {Message}", ex.Message);
        }
    }

    /// <summary>The saved guide for this DVR, if it is younger than <paramref name="maxAge"/>.</summary>
    public static (List<GuideAiring> Device, List<GuideAiring> Fast, DateTime SavedUtc)? Load(
        string? serverId, TimeSpan maxAge, ILogger log)
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            using var stream = System.IO.File.OpenRead(File);
            var saved = JsonSerializer.Deserialize<Saved>(stream);
            if (saved is null || saved.ServerId != serverId || DateTime.UtcNow - saved.SavedUtc > maxAge)
                return null;
            return (saved.Device, saved.Fast, saved.SavedUtc);
        }
        catch (Exception ex)
        {
            log.LogWarning("The saved guide could not be read: {Message}", ex.Message);
            return null;
        }
    }

    public static void Clear()
    {
        try { System.IO.File.Delete(File); } catch { /* nothing to do */ }
    }
}
