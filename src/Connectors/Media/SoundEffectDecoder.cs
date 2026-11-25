using System.Diagnostics;
using Application.Interfaces;
using Application.Models;
using Application.Services;

namespace Connectors.Media;

public sealed class SoundEffectDecoder : ISoundEffectDecoder
{
    public const int MaximumSeconds = 30;
    public async Task<byte[]> DecodeAsync(string file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(file);
        string[] extensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".webm"];
        if (!extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            throw new SoundboardException("Выберите аудиофайл WAV, MP3, OGG, FLAC, M4A, AAC или WebM.");
        if (!File.Exists(full)) throw new SoundboardException("Файл эффекта не найден. Выберите его заново.");
        if (new FileInfo(full).Length > 100 * 1024 * 1024) throw new SoundboardException("Файл эффекта должен быть меньше 100 МБ.");
        ProcessStartInfo info = new(Kvasir.Configuration.ApplicationPaths.RuntimeFile("ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe", "-i", full, "-vn", "-t", "30.1", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" }) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new SoundboardException("Не удалось запустить подготовку эффекта.");
        process.StandardInput.Close();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using MemoryStream pcm = new();
        try
        {
            byte[] buffer = new byte[16384];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (pcm.Length + count > MaximumSeconds * PcmEffectMixer.BytesPerSecond)
                    throw new SoundboardException("Эффект длиннее 30 секунд. Сначала обрежьте аудиофайл.");
                pcm.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || pcm.Length == 0 || pcm.Length % 4 != 0)
                throw new SoundboardException("Не удалось прочитать аудиофайл эффекта. Проверьте, что он не повреждён.");
            return pcm.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SoundboardException("Подготовка эффекта заняла слишком много времени. Попробуйте другой файл."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await errors;
        }
    }
}
