namespace SoundDeck.Core;

/// <summary>An output (render) device as shown in the device picker.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);
