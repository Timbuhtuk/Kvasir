using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using Application.Models;
using Application.Services;
using Connectors.Media;
using Infrastructure;
using Kvasir.Control;
using Kvasir.Desktop;
using Kvasir.Desktop.Services;
using Kvasir.Desktop.ViewModels;
using Tessera.WpfStyles.Themes;

// Opt-in UI fixture: actual WPF window and pipe clients, isolated library and synthetic PCM.
// It never connects to Discord or changes the user's configuration/library.
internal sealed class SoundboardPreview
{
    private readonly object _sync = new();
    private readonly PcmEffectMixer _mixer = new();
    private readonly SoundboardLibrary _library;
    private readonly string _session, _root;
    private int _musicVolume = 75, _effectsVolume = 75;
    private bool _duck, _paused;
    private double _position;
    private long _frames, _audibleFrames;

    private SoundboardPreview(string root, string session)
    {
        _root = root; _session = session;
        _library = new(new SoundEffectDecoder(), Path.Combine(root, "soundboard"));
    }
    internal static void Show()
    {
        string root = Path.Combine(Path.GetTempPath(), "kvasir-soundboard-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pipe = "Kvasir-Preview-" + Guid.NewGuid().ToString("N"), session = Guid.NewGuid().ToString("N");
        ProcessStartInfo info = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--soundboard-backend", pipe, session, root }) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        var app = new App(); app.InitializeComponent();
        typeof(System.Windows.Application).GetField("_startupUri", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, null);
        ThemeManager.Apply(TesseraTheme.Dark);
        var window = new MainWindow { Title = "Саундборд · локальная проверка без Discord" };
        var vm = (ControlViewModel)window.DataContext;
        var controller = (BotProcessController)typeof(ControlViewModel).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        typeof(BotProcessController).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, process);
        typeof(BotProcessController).GetProperty("PipeName")!.SetValue(controller, pipe);
        typeof(BotProcessController).GetProperty("Session")!.SetValue(controller, session);
        vm.OpenServer(new(1, "Локальная проверка")); vm.ViewIndex = 3;
        Console.WriteLine("PREVIEW DATA: " + root);
        try { app.Run(window); }
        finally
        {
            vm.Dispose();
            if (controller.Running)
            {
                process.StandardInput.WriteLine("stop"); process.StandardInput.Flush();
                if (!process.WaitForExit(5000)) process.Kill(true);
            }
        }
    }
    internal static async Task RunBackendAsync(string[] args)
    {
        var preview = new SoundboardPreview(args[3], args[2]);
        await preview._library.InitializeAsync();
        string source = Path.Combine(preview._root, "Колокольчик.wav");
        WriteWave(source, 3);
        await preview._library.ImportAsync(source, CancellationToken.None);
        using var lifetime = new CancellationTokenSource();
        Task normal = preview.PipeAsync(args[1], lifetime.Token), audio = preview.PipeAsync(args[1] + "-audio", lifetime.Token);
        Task render = preview.RenderAsync(lifetime.Token);
        await Console.In.ReadLineAsync(); lifetime.Cancel();
        try { await Task.WhenAll(normal, audio, render); } catch (OperationCanceledException) { }
    }
    private async Task PipeAsync(string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(token);
            try
            {
                while (pipe.IsConnected)
                {
                    var request = await ControlProtocol.ReadAsync<ControlRequest>(pipe, token);
                    if (request.Session != _session) throw new UnauthorizedAccessException();
                    ControlResponse response;
                    try
                    {
                        // Deliberately slow normal import proves it cannot block the audio pipe.
                        if (request.Action == "sound-import")
                        {
                            await Task.Delay(1500, token);
                            await _library.ImportAsync(request.Value!, token);
                        }
                        if (request.Action == "sound-remove") await _library.RemoveAsync(request.EffectId!.Value, token);
                        lock (_sync)
                        {
                            string message = request.Action switch
                            {
                                "sound-play" => _mixer.Play(_library.GetReady(request.EffectId!.Value)) ? "Эффект запущен поверх музыки." : throw new SoundboardException("Одновременно можно запустить до восьми эффектов."),
                                "sound-stop" => "Эффекты остановлены. Музыка продолжает играть.",
                                "sound-import" => "Эффект подготовлен и добавлен.",
                                _ => "Готово.",
                            };
                            switch (request.Action)
                            {
                                case "sound-stop": _mixer.Stop(); break;
                                case "sound-volume": _effectsVolume = (int)request.Number; break;
                                case "sound-music-volume": case "volume": _musicVolume = (int)request.Number; break;
                                case "sound-duck": _duck = request.Number > 0; break;
                                case "pause": _paused = true; break;
                                case "resume": _paused = false; break;
                            }
                            File.AppendAllText(Path.Combine(_root, "commands.log"), $"{DateTime.UtcNow:O} {request.Action} value={request.Number} voices={_mixer.ActiveCount} position={_position:F2}\n");
                            response = new(true, message, request.Action.StartsWith("sound-") && request.Action is not "sound-import" and not "sound-remove" ? null : Snapshot(), Mix: Mix());
                        }
                    }
                    catch (SoundboardException ex) { response = new(false, ex.Message, ErrorCode: "soundboard", Detail: ex.Message); }
                    await ControlProtocol.WriteAsync(pipe, response, token);
                }
            }
            catch (IOException) { }
        }
    }
    private AudioMixItem Mix() => new(_musicVolume, _effectsVolume, _duck, _mixer.ActiveCount);
    private ControlState Snapshot() => new()
    {
        Ready = true, GuildId = 1, Guilds = [new(1, "Локальная проверка")], Channels = [new(10, "Синтетический звук")], VoiceChannelId = 10,
        Current = new(1, "Тестовый тон · локальный сеанс", "Без подключения к Discord", 600, null),
        Status = _paused ? "Paused" : "Playing", Position = _position,
        Queue = [new(Guid.Empty, new(2, "Следующий тестовый трек", "Синтетический звук", 600, null), 1)],
        Settings = new(null, false, false, 10, _musicVolume), Mix = Mix(),
        Effects = _library.List().Select(e => new SoundEffectItem(e.Id, e.Name, e.Duration, e.Ready, e.Problem)).ToArray(),
    };
    private async Task RenderAsync(CancellationToken token)
    {
        // Capture real mixer output for local inspection; no speaker or network playback.
        using FileStream capture = File.Create(Path.Combine(_root, "mixed.pcm"));
        byte[] frame = new byte[PcmEffectMixer.FrameBytes];
        while (!token.IsCancellationRequested)
        {
            lock (_sync)
            {
                for (int q = 0; q < frame.Length; q += 4)
                {
                    short sample = _paused ? (short)0 : (short)(Math.Sin((_position * 48000 + q / 4) * 2 * Math.PI * 220 / 48000) * 1800);
                    BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(q), sample); BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(q + 2), sample);
                }
                _mixer.Mix(frame, frame.Length, _musicVolume, _effectsVolume, _duck);
                _frames++; if (frame.Any(b => b != 0)) _audibleFrames++;
                if (!_paused) _position += .02;
                if (_frames % 50 == 0) File.WriteAllText(Path.Combine(_root, "render.txt"), $"frames={_frames}; audible={_audibleFrames}; voices={_mixer.ActiveCount}; position={_position:F2}");
            }
            await capture.WriteAsync(frame, token);
            await Task.Delay(20, token);
        }
    }
    internal static void WriteWave(string path, int seconds)
    {
        int bytes = seconds * PcmEffectMixer.BytesPerSecond;
        using BinaryWriter writer = new(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)2);
        writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
        for (int q = 0; q < bytes / 4; q++)
        {
            short sample = (short)(Math.Sin(q * 2 * Math.PI * 880 / 48000) * 8000 * Math.Exp(-q / 60000d));
            writer.Write(sample); writer.Write(sample);
        }
    }
}
