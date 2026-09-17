using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabloFireTv.Updates;

/// <summary>Where to look for a newer release.</summary>
public sealed record UpdateSettings
{
    /// <summary>This app's own GitHub releases. Public, so no token is needed.</summary>
    public static readonly UpdateSettings Default = new()
    {
        ApiBase = "https://api.github.com",
        Owner = "ksaye",
        Repo = "tablo-firetv",
        TagPrefix = "v",
        InstallerExtension = ".apk"
    };

    public string ApiBase { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    public string TagPrefix { get; init; } = "";
    public string InstallerExtension { get; init; } = ".apk";
}

public sealed record UpdateCheck
{
    public required bool Available { get; init; }
    public Version? Version { get; init; }
    public string? Notes { get; init; }
    public string? DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public string? Problem { get; init; }

    public static UpdateCheck None(string? problem = null) => new() { Available = false, Problem = problem };
}

internal sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; init; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; init; } = [];
}

internal sealed record GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("browser_download_url")] public string Url { get; init; } = "";
}

/// <summary>
/// Checks GitHub for a newer release and fetches its APK. Deliberately quiet on failure: no
/// network, or GitHub's hourly limit for unauthenticated calls, is never worth bothering anyone with.
/// </summary>
public sealed class UpdateService(HttpClient http, UpdateSettings settings)
{
    public async Task<UpdateCheck> CheckAsync(Version current, CancellationToken cancel = default)
    {
        List<GitHubRelease>? releases;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{settings.ApiBase.TrimEnd('/')}/repos/{settings.Owner}/{settings.Repo}/releases?per_page=20");
            Authenticate(request);

            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheck.None($"The update server answered {(int)response.StatusCode}.");

            releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { return UpdateCheck.None(ex.Message); }
        catch (TaskCanceledException) { return UpdateCheck.None("The update server did not answer."); }
        catch (JsonException) { return UpdateCheck.None("The update server sent something unreadable."); }

        if (releases is null || releases.Count == 0) return UpdateCheck.None();

        var candidates = releases
            .Where(r => !r.Draft)
            .Select(r => new
            {
                Release = r,
                Version = VersionFor(r.TagName),
                Asset = r.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(settings.InstallerExtension, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .ToList();

        if (candidates.Count == 0) return UpdateCheck.None();

        var newest = candidates[0];
        if (newest.Version! <= current) return UpdateCheck.None();
        if (newest.Asset is null) return UpdateCheck.None("The newest release has no installer attached.");

        return new UpdateCheck
        {
            Available = true,
            Version = newest.Version,
            Notes = string.IsNullOrWhiteSpace(newest.Release.Body) ? newest.Release.Name : newest.Release.Body,
            DownloadUrl = newest.Asset.Url,
            FileName = newest.Asset.Name
        };
    }

    public async Task DownloadAsync(string url, string destination, CancellationToken cancel = default)
    {
        var partial = destination + ".part";
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            Authenticate(request);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, cancel).ConfigureAwait(false);
        }
        File.Move(partial, destination, overwrite: true);
    }

    private static void Authenticate(HttpRequestMessage request)
    {
        // GitHub's API refuses requests without a User-Agent.
        request.Headers.UserAgent.ParseAdd("tablo-firetv");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
    }

    private Version? VersionFor(string tag)
    {
        if (settings.TagPrefix.Length == 0 || !tag.StartsWith(settings.TagPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return ParseVersion(tag[settings.TagPrefix.Length..]);
    }

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var cut = text.IndexOfAny(['+', '-']);
        if (cut > 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? Normalise(version) : null;
    }

    public static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
