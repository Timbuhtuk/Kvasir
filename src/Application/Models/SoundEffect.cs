namespace Application.Models;

public sealed record SoundEffectInfo(Guid Id, string Name, double Duration, bool Ready = true, string? Problem = null);
public sealed record SoundEffectClip(SoundEffectInfo Info, ReadOnlyMemory<byte> Pcm);

public sealed class SoundboardException(string message) : Exception(message);
