Програмний код

В.1 Приклад оформлення програмного коду

GitHub репозиторій: https://github.com/NurePankovskyiTymofii/ark-pzpi-23-10-pankovskyi-tymofii/tree/main/Pract2/ark-pzpi-23-10-pankovskyi-tymofii-pract2/RefactoringExamples/ReplaceMagicNumberWithSymbolicConstant.cs

// ReplaceMagicNumberWithSymbolicConstant.cs
// БУЛО: Магічні числа в аргументах FFmpeg та розмірі буфера

ProcessStartInfo processStartInfo = new()
{
    FileName = ffmpegPath,
    // Магічні числа: 48000, 2, 8192
    Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 -",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};

using var ffmpegProcess = new Process { StartInfo = processStartInfo };
using var outputStream = new MemoryStream();

if (!ffmpegProcess.Start())
{
    await Logger.AddLog("FFMPEG STARTUP ERROR", LogLevel.ERROR);
    return null;
}

ffmpegProcess.BeginErrorReadLine();

// Магічне число: 8192 байти для буфера
byte[] buffer = new byte[8192];
int bytesRead;
while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream
    .ReadAsync(buffer, 0, buffer.Length)) > 0)
{
    await outputStream.WriteAsync(buffer, 0, bytesRead);
}

await ffmpegProcess.WaitForExitAsync();

В.2 Приклад оформлення програмного коду

GitHub репозиторій: https://github.com/NurePankovskyiTymofii/ark-pzpi-23-10-pankovskyi-tymofii/tree/main/Pract2/ark-pzpi-23-10-pankovskyi-tymofii-pract2/RefactoringExamples/ReplaceMagicNumberWithSymbolicConstant.cs

// ReplaceMagicNumberWithSymbolicConstant.cs
// СТАЛО: Магічні числа замінені на іменовані константи

// Константи для аудіо параметрів
private const int AUDIO_SAMPLE_RATE_HZ = 48000;  // Стандартна частота для Discord
private const int AUDIO_CHANNELS = 2;              // Стерео (2 канали)
private const int BUFFER_SIZE_BYTES = 8192;       // Розмір буфера (8 KB)

ProcessStartInfo processStartInfo = new()
{
    FileName = ffmpegPath,
    // Використовуємо константи замість магічних чисел
    Arguments = $"-i \"{file_path}\" -f s16le -ar {AUDIO_SAMPLE_RATE_HZ} -ac {AUDIO_CHANNELS} -",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};

using var ffmpegProcess = new Process { StartInfo = processStartInfo };
using var outputStream = new MemoryStream();

if (!ffmpegProcess.Start())
{
    await Logger.AddLog("FFMPEG STARTUP ERROR", LogLevel.ERROR);
    return null;
}

ffmpegProcess.BeginErrorReadLine();

// Використовуємо константу для розміру буфера
byte[] buffer = new byte[BUFFER_SIZE_BYTES];
int bytesRead;
while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream
    .ReadAsync(buffer, 0, buffer.Length)) > 0)
{
    await outputStream.WriteAsync(buffer, 0, bytesRead);
}

await ffmpegProcess.WaitForExitAsync();

В.3 Приклад оформлення програмного коду

GitHub репозиторій: https://github.com/NurePankovskyiTymofii/ark-pzpi-23-10-pankovskyi-tymofii/tree/main/Pract2/ark-pzpi-23-10-pankovskyi-tymofii-pract2/RefactoringExamples/ReplaceTempWithQuery.cs

// ReplaceTempWithQuery.cs
// БУЛО: Тимчасова змінна temp використовується для створення елемента черги

public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null)
{
    if (voiceChannel == null)
    {
        await Logger.AddLog("User not in voice chat", LogLevel.WARNING);
        return;
    }

    if (song_id != null)
    {
        Song? song = await _songRepository.GetByIdAsync(song_id);
        if (song == null)
            return;

        // Тимчасова змінна temp
        KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, song);
        music_queue.Enqueue(temp);
    }
    
    if (playlist_id != null)
    {
        Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlist_id.Value);
        if (playlist == null)
            return;
            
        foreach (Song s in playlist.Songs)
        {
            // Тимчасова змінна temp використовується повторно
            KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, s);
            music_queue.Enqueue(temp);
        }
    }
}

В.4 Приклад оформлення програмного коду

GitHub репозиторій: https://github.com/NurePankovskyiTymofii/ark-pzpi-23-10-pankovskyi-tymofii/tree/main/Pract2/ark-pzpi-23-10-pankovskyi-tymofii-pract2/RefactoringExamples/ReplaceTempWithQuery.cs

// ReplaceTempWithQuery.cs
// СТАЛО: Тимчасова змінна замінена на inline-створення та LINQ

public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null)
{
    if (voiceChannel == null)
    {
        await Logger.AddLog("User not in voice chat", LogLevel.WARNING);
        return;
    }

    if (song_id != null)
    {
        Song? song = await _songRepository.GetByIdAsync(song_id);
        if (song == null)
            return;

        // Варіант 1: Inline створення
        music_queue.Enqueue(new KeyValuePair<IVoiceChannel, Song>(voiceChannel, song));
    }
    
    if (playlist_id != null)
    {
        Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlist_id.Value);
        if (playlist == null)
            return;
            
        // Використовуємо LINQ для створення елементів черги
        var queueItems = playlist.Songs
            .Select(s => new KeyValuePair<IVoiceChannel, Song>(voiceChannel, s));
        foreach (var item in queueItems)
        {
            music_queue.Enqueue(item);
        }
    }
}

В.5 Приклад оформлення програмного коду

GitHub репозиторій: https://github.com/NurePankovskyiTymofii/ark-pzpi-23-10-pankovskyi-tymofii/tree/main/Pract2/ark-pzpi-23-10-pankovskyi-tymofii-pract2/RefactoringExamples/ReplaceNestedConditionalWithGuardClauses.cs

// ReplaceNestedConditionalWithGuardClauses.cs
// БУЛО: Вкладені умови роблять код складним для розуміння

public async Task JoinAsync(IVoiceChannel channel)
{
    await Logger.AddLog($"JoinAsync to voice channel {channel.Name} called");

    // Вкладені умови роблять код складним для розуміння
    if (audio_client != null && 
        audio_client.ConnectionState == ConnectionState.Connected && 
        current_voice_channel != channel)
    {
        await audio_client.StopAsync();
        audio_client = await channel.ConnectAsync();
        current_voice_channel = channel;
    }
    else if (audio_client != null && 
             audio_client.ConnectionState == ConnectionState.Connecting && 
             current_voice_channel != channel)
    {
        await Task.Delay(CONNECTION_WAIT_MS);
        await audio_client.StopAsync();
        audio_client = await channel.ConnectAsync();
        current_voice_channel = channel;
    }
    else if (audio_client != null && 
             audio_client.ConnectionState == ConnectionState.Disconnecting)
    {
        await Task.Delay(CONNECTION_WAIT_MS);
        audio_client = await channel.ConnectAsync();
        current_voice_channel = channel;
    }
    else
    {
        audio_client = await channel.ConnectAsync();
        current_voice_channel = channel;
    }
}

В.6 Приклад оформлення програмного коду

GitHub репозиторій: https://github.com/NurePankovskyiTymofii/ark-pzpi-23-10-pankovskyi-tymofii/tree/main/Pract2/ark-pzpi-23-10-pankovskyi-tymofii-pract2/RefactoringExamples/ReplaceNestedConditionalWithGuardClauses.cs

// ReplaceNestedConditionalWithGuardClauses.cs
// СТАЛО: Guard clauses спрощують логіку та роблять код більш читабельним

public async Task JoinAsync(IVoiceChannel channel)
{
    await Logger.AddLog($"JoinAsync to voice channel {channel.Name} called");

    // Guard clause: якщо клієнта немає, просто підключаємося
    if (audio_client == null)
    {
        audio_client = await channel.ConnectAsync();
        current_voice_channel = channel;
        return;
    }

    // Guard clause: якщо вже підключені до цього каналу, нічого не робимо
    if (current_voice_channel == channel && 
        audio_client.ConnectionState == ConnectionState.Connected)
    {
        return;
    }

    // Guard clause: якщо йде підключення або відключення, чекаємо
    if (audio_client.ConnectionState == ConnectionState.Connecting || 
        audio_client.ConnectionState == ConnectionState.Disconnecting)
    {
        await Task.Delay(CONNECTION_WAIT_MS);
    }

    // Guard clause: якщо підключені, відключаємося перед перепідключенням
    if (audio_client.ConnectionState != ConnectionState.Disconnected)
    {
        await audio_client.StopAsync();
    }

    // Основна логіка: підключаємося до нового каналу
    audio_client = await channel.ConnectAsync();
    current_voice_channel = channel;
}
