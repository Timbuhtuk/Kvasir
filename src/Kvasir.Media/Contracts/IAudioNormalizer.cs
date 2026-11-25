namespace Kvasir.Media;

public interface IAudioNormalizer
{
    Task NormalizeAsync(string source, string destination, CancellationToken cancellationToken);
}
