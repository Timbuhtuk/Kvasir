using System.Buffers.Binary;
using Application.Models;

namespace Application.Services;

// One writer mixes all sources before Opus encoding. Clips are already decoded and resident in memory.
public sealed class PcmEffectMixer
{
    public const int FrameBytes = 3840, BytesPerSecond = 192000, MaximumVoices = 8;
    private readonly object _sync = new();
    private readonly List<Voice> _voices = [];
    private double _duck = 1, _limiter = 1;
    public int ActiveCount { get { lock (_sync) return _voices.Count; } }
    public bool Play(SoundEffectClip clip)
    {
        if (clip.Pcm.Length == 0 || clip.Pcm.Length % 4 != 0) throw new SoundboardException("Эффект содержит некорректное аудио.");
        lock (_sync)
        {
            if (_voices.Count >= MaximumVoices) return false;
            _voices.Add(new(clip.Pcm));
            return true;
        }
    }
    public void Stop() { lock (_sync) _voices.Clear(); }

    public void Mix(Span<byte> frame, int musicBytes, int musicVolume, int effectsVolume, bool duckMusic)
    {
        if (frame.Length != FrameBytes || musicBytes < 0 || musicBytes > frame.Length || musicBytes % 4 != 0)
            throw new ArgumentException("Нужен полный 20-мс стереокадр PCM.");
        lock (_sync)
        {
            frame[musicBytes..].Clear();
            Span<double> mixed = stackalloc double[FrameBytes / 2];
            double musicGain = Math.Clamp(musicVolume, 0, 100) / 100d, effectGain = Math.Clamp(effectsVolume, 0, 100) / 100d;
            bool audibleEffects = effectGain > 0 && _voices.Count > 0;
            double target = duckMusic && audibleEffects ? .3 : 1;
            // 80 ms attack, 250 ms release. Both stereo channels share the same gain.
            double step = .7 / (48000 * (target < _duck ? .08 : .25));
            double peak = 0;
            for (int q = 0; q < mixed.Length; q += 2)
            {
                _duck = target < _duck ? Math.Max(target, _duck - step) : Math.Min(target, _duck + step);
                for (int e = 0; e < 2; e++)
                {
                    int sample = q + e;
                    double value = BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(sample * 2, 2)) * musicGain * _duck;
                    foreach (Voice voice in _voices)
                        if (voice.Offset + sample * 2 + 2 <= voice.Pcm.Length)
                            value += BinaryPrimitives.ReadInt16LittleEndian(voice.Pcm.Span.Slice(voice.Offset + sample * 2, 2)) * effectGain;
                    mixed[sample] = value; peak = Math.Max(peak, Math.Abs(value));
                }
            }
            double limit = peak > short.MaxValue ? short.MaxValue / peak : 1;
            _limiter = Math.Min(limit, _limiter + .04);
            for (int q = 0; q < mixed.Length; q++)
                BinaryPrimitives.WriteInt16LittleEndian(frame.Slice(q * 2, 2), (short)Math.Clamp(Math.Round(mixed[q] * _limiter), short.MinValue, short.MaxValue));
            foreach (Voice voice in _voices) voice.Offset += FrameBytes;
            _voices.RemoveAll(v => v.Offset >= v.Pcm.Length);
        }
    }
    private sealed class Voice(ReadOnlyMemory<byte> pcm)
    {
        public ReadOnlyMemory<byte> Pcm { get; } = pcm;
        public int Offset { get; set; }
    }
}
