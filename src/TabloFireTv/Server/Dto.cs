using TabloFireTv.Models;
using TabloFireTv.Services;

namespace TabloFireTv;

// Shapes handed to the browser. Deliberately flat and pre-formatted: the page should be able
// to render straight from these without knowing anything about the Tablo's own JSON.

public sealed record ChannelDto(
    string Path, string Number, string CallSign, string Name, string? Network, string? Resolution,
    /// <summary>A free streaming channel rather than an antenna one — watchable, never recordable.</summary>
    bool IsFast)
{
    public static ChannelDto From(GuideChannelWrap w) => new(
        w.Path, w.Channel.DisplayNumber, w.Channel.CallSign,
        string.IsNullOrWhiteSpace(w.Channel.Name) ? w.Channel.CallSign : w.Channel.Name,
        w.Channel.Network, w.Channel.Resolution,
        TabloClient.IsFast(w.Path));
}

public sealed record RecordingDto(
    string Path, string Title, string? Subtitle, string Description,
    string? Start, int DurationSeconds, string? ChannelNumber, string? ChannelCallSign,
    long? ImageId, bool Watched, bool Protected, long SizeBytes, string? State,
    int ResumeSeconds, string Kind)
{
    public static RecordingDto From(RecordingAiring r)
    {
        var ch = r.AiringDetails.Channel?.Channel;

        // A recording is a series episode, a sporting event or a movie, and only one of those
        // sub-objects is populated. Titles come from whichever it is.
        var (title, subtitle, kind) =
            r.MovieAiring?.Title is { Length: > 0 } movie
                ? (movie, r.MovieAiring.ReleaseYear?.ToString(), "movie")
            : r.Event?.Title is { Length: > 0 } sport
                ? (r.AiringDetails.ShowTitle ?? r.ShowTitle ?? sport, sport, "sport")
            : (r.AiringDetails.ShowTitle ?? r.ShowTitle ?? r.Episode?.Title ?? "Recording",
               EpisodeLabel(r.Episode), "episode");

        // Prefer what was actually recorded over what was scheduled — a clipped recording
        // should show the length you can really watch.
        var duration = r.VideoDetails.Duration > 0 ? r.VideoDetails.Duration : r.AiringDetails.Duration;

        return new RecordingDto(
            r.Path, title, subtitle,
            r.Episode?.Description ?? r.Event?.Description ?? r.MovieAiring?.Description ?? "",
            r.AiringDetails.Datetime, duration,
            ch?.DisplayNumber, ch?.CallSign,
            r.SnapshotImage?.ImageId, r.UserInfo?.Watched ?? false, r.UserInfo?.Protected ?? false,
            r.VideoDetails.Size, r.VideoDetails.State,
            r.UserInfo?.Position ?? 0, kind);
    }

    private static string? EpisodeLabel(EpisodeInfo? e)
    {
        if (e is null) return null;
        var num = e is { SeasonNumber: > 0, Number: > 0 } ? $"S{e.SeasonNumber}E{e.Number}" : null;
        return string.Join(" · ", new[] { num, e.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}

public sealed record AiringDto(
    string Path, string ChannelPath, string Title, string? Subtitle, string Description,
    string Start, int DurationSeconds, string ScheduleState, bool IsNew, bool IsMovie, int? Year)
{
    public static AiringDto? From(GuideAiring a)
    {
        var start = a.AiringDetails.Datetime;
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(a.AiringDetails.ChannelPath))
            return null;

        var subtitle = a.MovieAiring is not null
            ? a.MovieAiring.ReleaseYear?.ToString()
            : a.Episode is { } e
                ? string.Join(" · ", new[]
                  {
                      e is { SeasonNumber: > 0, Number: > 0 } ? $"S{e.SeasonNumber}E{e.Number}" : null,
                      e.Title
                  }.Where(s => !string.IsNullOrWhiteSpace(s)))
                : a.Event?.Title;

        return new AiringDto(
            a.Path, a.AiringDetails.ChannelPath!, a.Title,
            string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            a.Description, start, a.AiringDetails.Duration,
            a.Schedule.State,
            a.Qualifiers.Contains("new", StringComparer.OrdinalIgnoreCase),
            a.MovieAiring is not null, a.MovieAiring?.ReleaseYear);
    }

    public DateTime StartUtc => TabloClient.ParseDate(Start);
    public DateTime EndUtc => StartUtc.AddSeconds(DurationSeconds);
}

/// <summary>What is on each channel right now — the Live tab's whole payload.</summary>
public sealed record NowDto(ChannelDto Channel, AiringDto? Airing, double Progress);

public sealed record StatusDto(
    bool Connected, string State, string? DeviceName, string? DeviceHost, string? Model,
    string? Firmware, int Tuners, bool GuideReady, double? GuideProgress,
    long StorageTotalBytes, long StorageFreeBytes, int ActiveStreams,
    /// <summary>No Tablo account has been given yet, so the page should offer the sign-in.</summary>
    bool NeedsSetup);

public sealed record PlayDto(
    string SessionId, string Url, bool Live, double OffsetSeconds, int DurationSeconds);
