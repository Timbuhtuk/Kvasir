using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace Kvasir.Media;

public sealed class BufferedPcmStream : Stream
{
    private const int BytesPerSecond = 192000;
    private const int ChunkSize = BytesPerSecond / 10;
    private const int BufferedChunks = 10;
    private readonly FileStream _source;
    private CancellationTokenSource? _decodeCancellation;
    private Task _decode = Task.CompletedTask;
    private ChannelReader<ReadOnlyMemory<byte>>? _reader;
    private ReadOnlyMemory<byte> _remaining;
    private long _position;
    private bool _disposed;

    private BufferedPcmStream(FileStream source) => _source = source;

    public static async Task<BufferedPcmStream> OpenAsync(FileStream source, CancellationToken token)
    {
        var stream = new BufferedPcmStream(source);
        try
        {
            await stream.SeekAsync(0, token);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task SeekAsync(long bytePosition, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopDecoderAsync();
        token.ThrowIfCancellationRequested();
        _position = Math.Max(0, bytePosition) / 4 * 4;
        _remaining = default;

        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(BufferedChunks)
        {
            SingleReader = true,
            SingleWriter = true
        });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader = channel.Reader;
        _decodeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken decodeToken = _decodeCancellation.Token;
        long startPosition = _position;
        _decode = Task.Run(() => DecodeAsync(channel.Writer, ready, startPosition, decodeToken));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    private async Task DecodeAsync(ChannelWriter<ReadOnlyMemory<byte>> writer, TaskCompletionSource ready,
        long position, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            var info = new ProcessStartInfo(MediaTools.RuntimeFile("ffmpeg.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string seconds = ((decimal)position / BytesPerSecond).ToString(CultureInfo.InvariantCulture);
            string[] arguments =
            [
                "-nostdin", "-loglevel", "error", "-ss", seconds, "-i", _source.Name,
                "-vn", "-af", FfmpegInteractor.NormalizationFilter, "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1"
            ];
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using Process process = Process.Start(info) ?? throw new IOException("FFmpeg не запустился.");
            using var stop = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            });
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            {
                Stream output = process.StandardOutput.BaseStream;
                int buffered = 0;
                while (true)
                {
                    byte[] chunk = new byte[ChunkSize];
                    int count = await output.ReadAtLeastAsync(chunk, chunk.Length, throwOnEndOfStream: false, token);
                    if (count == 0) break;
                    await writer.WriteAsync(chunk.AsMemory(0, count), token);
                    if (++buffered == BufferedChunks) ready.TrySetResult();
                }

                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0)
                    throw new IOException($"FFmpeg ({process.ExitCode}): {(await error).Trim()}");
                if (buffered == 0 && position == 0)
                    throw new IOException("FFmpeg не вернул аудиоданные.");
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                await error;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ready.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            failure = ex;
            ready.TrySetException(ex);
        }
        finally
        {
            writer.TryComplete(failure);
            ready.TrySetResult();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return 0;
        if (_remaining.IsEmpty)
        {
            if (!await _reader!.WaitToReadAsync(cancellationToken)) return 0;
            _reader.TryRead(out _remaining);
        }

        int count = Math.Min(buffer.Length, _remaining.Length);
        _remaining[..count].CopyTo(buffer);
        _remaining = _remaining[count..];
        _position += count;
        return count;
    }

    private async Task StopDecoderAsync()
    {
        if (_decodeCancellation == null) return;
        using CancellationTokenSource cancellation = _decodeCancellation;
        _decodeCancellation = null;
        await cancellation.CancelAsync().ConfigureAwait(false);
        await _decode.ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopDecoderAsync(); }
        finally { await _source.DisposeAsync(); }
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { StopDecoderAsync().GetAwaiter().GetResult(); }
            finally { _source.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override long Length => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
