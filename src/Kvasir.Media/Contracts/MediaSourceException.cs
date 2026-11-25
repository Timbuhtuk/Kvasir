namespace Kvasir.Media;

public sealed class MediaSourceException(string message, string code = "source") : Exception(message)
{
    public string Code { get; } = code;
}
