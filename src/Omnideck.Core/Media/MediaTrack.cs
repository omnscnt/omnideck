namespace Omnideck.Core.Media;

/// <summary>A track an app is playing: title, artist, cover (encoded image), and where in it playback is.</summary>
/// <param name="Position">Where playback was when read; <paramref name="Duration"/> is zero when the app doesn't say.</param>
public sealed record MediaTrack(string AppId, string Title, string Artist, byte[]? Cover, TimeSpan Position, TimeSpan Duration);
