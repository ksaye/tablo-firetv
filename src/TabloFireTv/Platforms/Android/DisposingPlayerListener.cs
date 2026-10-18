using AndroidX.Media3.Common;

namespace TabloFireTv;

/// <summary>
/// Base for the app's <see cref="IPlayerListener"/>. It takes each Java object ExoPlayer hands a
/// callback and lets go of it straight away.
///
/// <para><b>Without this the app ran out of memory during long live viewing.</b> Java calls EVERY
/// listener method, including the ones a C# listener leaves to the interface defaults. Each call
/// wraps its arguments in managed objects that hold a JNI global reference until the .NET garbage
/// collector runs, and this app allocates so little managed memory that it hardly ever does.
/// Meanwhile <c>onTimelineChanged</c> fires on every refresh of the Tablo's live playlist, which
/// only grows, and each <c>Timeline</c> holds all of it. Measured on a 2nd-gen Fire TV Stick: the
/// Java heap climbed ~0.6 MB/s to its 192 MB limit and the app was killed; a heap dump showed 209
/// pinned timelines holding 478,837 playlist segments. Disposing only drops our reference -
/// ExoPlayer keeps using the object.</para>
///
/// <para>Never dispose the <c>player</c> argument: it is the same object as our own player field.
/// Override a method to use its argument, and call the base afterwards.</para>
/// </summary>
internal abstract class DisposingPlayerListener : Java.Lang.Object, IPlayerListener
{
    public virtual void OnEvents(IPlayer? player, PlayerEvents? events) => events?.Dispose();

    public virtual void OnTimelineChanged(Timeline? timeline, int reason) => timeline?.Dispose();

    public virtual void OnPositionDiscontinuity(PlayerPositionInfo? oldPosition, PlayerPositionInfo? newPosition, int reason)
    {
        oldPosition?.Dispose();
        newPosition?.Dispose();
    }

    public virtual void OnMediaItemTransition(MediaItem? mediaItem, int reason) => mediaItem?.Dispose();

    public virtual void OnTracksChanged(Tracks? tracks) => tracks?.Dispose();

    public virtual void OnMediaMetadataChanged(MediaMetadata? mediaMetadata) => mediaMetadata?.Dispose();

    public virtual void OnMetadata(Metadata? metadata) => metadata?.Dispose();

    public virtual void OnVideoSizeChanged(VideoSize? videoSize) => videoSize?.Dispose();

    public virtual void OnPlaybackParametersChanged(PlaybackParameters? playbackParameters) => playbackParameters?.Dispose();

    public virtual void OnAvailableCommandsChanged(PlayerCommands? availableCommands) => availableCommands?.Dispose();
}
