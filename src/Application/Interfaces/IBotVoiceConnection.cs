namespace Application.Interfaces;

public interface IBotVoiceConnection : IAsyncDisposable
{
    Stream CreatePcmStream();
}
