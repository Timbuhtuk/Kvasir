using System.IO.Pipes;
using Kvasir.Control;

namespace Kvasir.Desktop.Services;

public sealed class BotControlClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ControlResponse> SendAsync(string pipeName, ControlRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe is not { IsConnected: true })
            {
                _pipe?.Dispose();
                _pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await _pipe.ConnectAsync(1000, cancellationToken);
            }
            await ControlProtocol.WriteAsync(_pipe, request, cancellationToken);
            return await ControlProtocol.ReadAsync<ControlResponse>(_pipe, cancellationToken);
        }
        catch
        {
            _pipe?.Dispose(); _pipe = null;
            // Mutations are never automatically retried: the bot may already have applied them.
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { _pipe?.Dispose(); _pipe = null; }
}
