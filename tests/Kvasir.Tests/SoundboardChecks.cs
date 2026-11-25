using System.Buffers.Binary;
using Application.Interfaces;
using Application.Models;
using Application.Services;
using Entities.Models;
using Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Kvasir.Tests;

internal static partial class Program
{
    private static byte[] ConstantPcm(short left, short right, int frames = 1)
    {
        byte[] bytes = new byte[PcmEffectMixer.FrameBytes * frames];
        for (int q = 0; q < bytes.Length; q += 4)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(q), left);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(q + 2), right);
        }
        return bytes;
    }
    private static SoundEffectClip Clip(byte[] pcm) => new(new(Guid.NewGuid(), "Effect", pcm.Length / 192000d), pcm);
    private static short Sample(byte[] frame, int index = 0) => BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(index * 2));
    private static async Task TestSoundboardAsync(ServiceProvider provider, string root)
    {
        PcmEffectMixer mixer = new();
        byte[] frame = ConstantPcm(1000, -1000), original = frame.ToArray();
        mixer.Mix(frame, frame.Length, 100, 100, false);
        Check(frame.SequenceEqual(original), "Dry music unchanged without effects");
        mixer.Play(Clip(ConstantPcm(2000, 4000, 10)));
        mixer.Mix(frame, frame.Length, 50, 25, false);
        Check(Sample(frame) == 1000 && Sample(frame, 1) == 500, "Music and effects have independent gain and preserve stereo");
        mixer.Stop(); frame = original.ToArray(); mixer.Mix(frame, frame.Length, 100, 100, false);
        Check(mixer.ActiveCount == 0 && frame.SequenceEqual(original), "Stopping effects preserves dry music");
        mixer.Play(Clip(ConstantPcm(0, 0, 30)));
        for (int q = 0; q < 5; q++) { frame = original.ToArray(); mixer.Mix(frame, frame.Length, 100, 100, true); }
        Check(Sample(frame, 1918) == 300 && Sample(frame, 1919) == -300, "Ducking reaches 30 percent with shared stereo envelope");
        mixer.Stop();
        for (int q = 0; q < 14; q++) { frame = original.ToArray(); mixer.Mix(frame, frame.Length, 100, 100, true); }
        Check(Sample(frame, 1918) == 1000, "Music returns to original gain after effects stop");
        mixer.Play(Clip(ConstantPcm(0, 0, 2))); frame = original.ToArray(); mixer.Mix(frame, frame.Length, 100, 0, true);
        Check(frame.SequenceEqual(original), "Muted effects do not duck music"); mixer.Stop();
        for (int q = 0; q < PcmEffectMixer.MaximumVoices; q++) Check(mixer.Play(Clip(ConstantPcm(30000, -30000, 2))), "Overlapping effect accepted");
        Check(!mixer.Play(Clip(original)), "Realtime mixer bounds simultaneous effects");
        frame = ConstantPcm(30000, -30000); mixer.Mix(frame, frame.Length, 100, 100, false);
        Check(Sample(frame) == 32767 && Sample(frame, 1) == -32767, "Peak limiter avoids signed overflow in summed audio");
        mixer = new(); mixer.Play(Clip(ConstantPcm(1234, -2345)[..8])); frame = new byte[PcmEffectMixer.FrameBytes];
        mixer.Mix(frame, 0, 100, 100, false);
        Check(Sample(frame) == 1234 && Sample(frame, 1) == -2345 && Sample(frame, 4) == 0 && mixer.ActiveCount == 0, "Short final effect frame padded with silence");

        string catalog = Path.Combine(root, "soundboard"), source = Path.Combine(root, "test-effect.wav");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var decoder = new TestEffectDecoder { Pcm = ConstantPcm(100, 200, 2) };
        var board = new SoundboardLibrary(decoder, catalog);
        var info = await board.ImportAsync(source, CancellationToken.None);
        Check(info.Ready && board.GetReady(info.Id).Pcm.Span.SequenceEqual(decoder.Pcm), "Import prepares effect before advertising readiness");
        var reloaded = new SoundboardLibrary(new TestEffectDecoder { Reject = true }, catalog);
        await reloaded.InitializeAsync();
        Check(reloaded.GetReady(info.Id).Pcm.Length == decoder.Pcm.Length, "Restart preloads saved PCM without decoding source again");
        await reloaded.RemoveAsync(info.Id, CancellationToken.None);
        Check(File.Exists(source) && reloaded.List().Count == 0, "Removing effect retains original source file");
        var corrupt = await board.ImportAsync(source, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(catalog, corrupt.Id.ToString("N") + ".pcm"), [1, 2, 3]);
        reloaded = new(new TestEffectDecoder(), catalog); await reloaded.InitializeAsync();
        Check(reloaded.List().Any(e => e.Id == corrupt.Id && !e.Ready && e.Problem != null), "Corrupt cache becomes disabled card with recovery information");
        bool failed = false;
        try { reloaded.GetReady(corrupt.Id); } catch (SoundboardException) { failed = true; }
        Check(failed, "Corrupt effect cannot enter realtime mixer");

        var prefs = provider.GetRequiredService<IMusicPreferences>();
        await prefs.SaveSettingsAsync(2, new() { Volume = 100, EffectsVolume = 100, DuckMusic = true });
        Check(prefs.GetSettings(2) is { EffectsVolume: 100, DuckMusic: true }, "Mixer settings survive preference copies");
        FakeConnector connector = new(); FakePlaybackCache cache = new() { Sample = 1000 };
        await using TrackService tracks = new(provider.GetRequiredService<IServiceScopeFactory>(), cache);
        await using MusicClientService music = new(new Guild { DiscordId = 2, Name = "Two" }, connector, provider.GetRequiredService<IServiceScopeFactory>(), tracks, prefs);
        MusicActor actor = new(999, 20, true); BotVoiceChannel channel = new(20, 2, "voice");
        Song song = new() { Id = 31, Name = "Music", Duration = 10 };
        await music.EnqueueAsync(channel, actor, songs: [song, song]); await Until(() => music.Status == Entities.Enums.PlaybackStatus.Playing);
        Check(music.PlayEffect(channel, actor, Clip(ConstantPcm(2000, 2000, 100))).Success, "Effect starts over active music");
        await Until(() => connector.LastVoice!.Frames.Any(f => Sample(f) > 2000));
        Check(music.Current?.Song.Id == 31 && music.Queue.Snapshot().Count == 1, "Effect preserves music and queue");
        await music.PauseAsync(actor, true); await Task.Delay(30); double position = music.PositionSeconds;
        music.StopEffects(actor); connector.LastVoice!.Frames.Clear();
        Check(music.PlayEffect(channel, actor, Clip(ConstantPcm(2000, 2000, 100))).Success, "Effect starts while music paused");
        await Until(() => connector.LastVoice.Frames.Any(f => Sample(f) == 2000));
        Check(music.PositionSeconds == position && music.Status == Entities.Enums.PlaybackStatus.Paused, "Effect does not advance paused music");
        music.StopEffects(actor); await Task.Delay(25); connector.LastVoice.Frames.Clear(); await Task.Delay(25);
        Check(music.ActiveEffects == 0 && connector.LastVoice.Frames.All(f => Sample(f) == 0), "Stop effects silences overlays without changing music pause");
        await music.PauseAsync(actor, false); await Until(() => music.PositionSeconds > position);
        Check(music.Current?.Song.Id == 31, "Music resumes same track after stopping effects");
        await music.StopAsync(actor, true);
        Check(music.PlayEffect(channel, actor, Clip(ConstantPcm(1234, 1234, 3))).Success, "Effects can play without queued music");
        await Until(() => connector.LastVoice!.Frames.Any(f => Sample(f) == 1234));
        await Until(() => music.ActiveEffects == 0);
        Check(music.Current == null && music.Queue.Snapshot().Count == 0 && music.History.All(s => s.Name != "Effect"), "Effects do not enter music history or queue");
        Check(!music.PlayEffect(channel, new(100, 99), Clip(original)).Success, "Wrong voice participant cannot play effects");
        await music.StopAsync(actor, true);
    }
    private sealed class TestEffectDecoder : ISoundEffectDecoder
    {
        public byte[] Pcm { get; set; } = ConstantPcm(1, 2);
        public bool Reject { get; set; }
        public Task<byte[]> DecodeAsync(string file, CancellationToken cancellationToken) => Reject ? throw new InvalidOperationException("Must not decode cached effects") : Task.FromResult(Pcm);
    }
    private static async Task TestNativeSoundEffectsAsync(string root)
    {
        static void Wave(string path, int seconds)
        {
            using BinaryWriter writer = new(File.Create(path));
            int bytes = 22050 * 2 * seconds;
            writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(22050); writer.Write(44100);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
            for (int q = 0; q < bytes / 2; q++) writer.Write((short)(Math.Sin(q * 2 * Math.PI * 440 / 22050) * 2000));
        }
        var decoder = new Connectors.Media.SoundEffectDecoder();
        string source = Path.Combine(root, "эффект mono 22k.wav"); Wave(source, 1);
        byte[] decoded = await decoder.DecodeAsync(source, CancellationToken.None);
        Check(decoded.Length == PcmEffectMixer.BytesPerSecond, "Real effect decoder resamples mono 22k to stereo 48k, including Unicode paths");
        Check(decoded.Any(b => b != 0) && Enumerable.Range(0, decoded.Length / 4).All(q => Sample(decoded, q * 2) == Sample(decoded, q * 2 + 1)), "Real effect decoder duplicates mono into both channels");
        string longSource = Path.Combine(root, "too-long.wav"); Wave(longSource, 31);
        bool rejected = false;
        try { await decoder.DecodeAsync(longSource, CancellationToken.None); }
        catch (SoundboardException ex) { rejected = ex.Message.Contains("30 секунд"); }
        Check(rejected, "Real decoder rejects effects longer than 30 seconds instead of silently truncating");
        string broken = Path.Combine(root, "broken.wav"); await File.WriteAllTextAsync(broken, "not audio"); rejected = false;
        try { await decoder.DecodeAsync(broken, CancellationToken.None); }
        catch (SoundboardException ex) { rejected = ex.Message.Contains("повреждён"); }
        Check(rejected, "Real decoder rejects corrupt audio with actionable error");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); rejected = false;
        try { await decoder.DecodeAsync(source, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "Cancelled preparation cannot import an effect");
    }
}
