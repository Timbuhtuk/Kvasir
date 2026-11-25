using Application.Models;

namespace Application.Interfaces;

public interface ISoundEffectDecoder
{
    Task<byte[]> DecodeAsync(string file, CancellationToken cancellationToken);
}

public interface ISoundboardLibrary
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<SoundEffectInfo> List();
    SoundEffectClip GetReady(Guid id);
    Task<SoundEffectInfo> ImportAsync(string file, CancellationToken cancellationToken);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken);
}
