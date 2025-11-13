---
marp: true
theme: gaia
paginate: true
---

<!-- _class: lead -->

# Рефакторинг коду на мові C#
# на прикладі музичного боту для Discord
**Виконав:** _Панковський Тимофій_


---
<!-- _class: lead -->
## Що таке рефакторинг?

- Покращення структури коду без зміни функціональності
- Підвищення читабельності та підтримуваності
- Спрощення розуміння логіки
- Зниження технічного боргу

---
<!-- _class: lead -->
## Три техніки рефакторингу

1. **Replace Magic Number with Symbolic Constant**
   - Заміна магічних чисел на іменовані константи

2. **Replace Temp with Query**
   - Заміна тимчасових змінних на методи або LINQ-запити

3. **Replace Nested Conditional with Guard Clauses**
   - Спрощення вкладених умов через ранні повернення

---
<!-- _class: lead -->
# Replace Magic Number with Symbolic Constant

---
<!-- _class: lead -->
# Проблема: Магічні числа

- Числові константи без явного значення
- Незрозуміло, що означає число
- Складно змінювати за потреби
- Високий ризик помилок

```csharp
Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 -";
byte[] buffer = new byte[8192];
```

**Що означають ці числа? Чому саме 48000? Чому 8192?**

---
<!-- _class: lead -->

# Рішення: Іменовані константи

- Створюємо константи з зрозумілими іменами
- Додаємо коментарі з поясненням
- Спрощуємо зміну значень
- Підвищуємо читабельність коду

---

## Приклад: FfmpegInteractor (БУЛО)

```csharp
public static async Task<byte[]?> ConvertMp3ToPcmBytes(string file_path)
{
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
    // Магічне число: 8192 байти для буфера
    byte[] buffer = new byte[8192];
}
```


---

## Приклад: FfmpegInteractor (СТАЛО)

```csharp
// Константи для аудіо параметрів
private const int AUDIO_SAMPLE_RATE_HZ = 48000;  // Стандартна частота для Discord
private const int AUDIO_CHANNELS = 2;              // Стерео (2 канали)
private const int BUFFER_SIZE_BYTES = 8192;       // Розмір буфера (8 KB)

public static async Task<byte[]?> ConvertMp3ToPcmBytes(string file_path)
{
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

    // Використовуємо константу для розміру буфера
    byte[] buffer = new byte[BUFFER_SIZE_BYTES];
}
```
---
<!-- _class: lead -->

# Replace Temp with Query

---
<!-- _class: lead -->

# Проблема: Тимчасові змінні

- Тимчасові змінні ускладнюють код
- Приховують наміри розробника
- Збільшують кількість рядків коду
- Можуть бути замінені на методи або LINQ

```csharp
KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, song);
music_queue.Enqueue(temp);
```

**Навіщо потрібна змінна `temp`?**

---
<!-- _class: lead -->

# Рішення: Методи та LINQ-запити

- Замінюємо тимчасові змінні на методи
- Використовуємо LINQ для перетворень
- Застосовуємо inline-створення об'єктів
- Спрощуємо код

---

## Приклад: MusicClient (БУЛО)

```csharp
public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null)
{
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
```

**Проблеми:** Зайва змінна | Дублювання коду | Незрозуміле призначення

---

## Приклад: MusicClient (СТАЛО)

```csharp
public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null)
{
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
```

**Переваги:** ✅ Прибрана змінна | ✅ Декларативний код | ✅ LINQ спрощує логіку

---
<!-- _class: lead -->

# Частина 3: Replace Nested Conditional with Guard Clauses

---
<!-- _class: lead -->

# Проблема: Вкладені умови

- Вкладені if-else ускладнюють читання
- Складно зрозуміти логіку виконання
- Збільшується когнітивне навантаження
- Складніше тестувати

```csharp
if (condition1)
{
    if (condition2)
    {
        if (condition3)
        {
            // основна логіка
        }
    }
}
```

---
<!-- _class: lead -->

# Рішення: Guard Clauses (Ранні повернення)

- Використовуємо ранні повернення (return)
- Перевіряємо виняткові випадки на початку
- Спрощуємо основну логіку
- Зменшуємо рівень вкладеності

**Принцип:** "Спочатку перевіряємо, що щось не так, і виходимо. Потім виконуємо основну логіку."

---

## Приклад: MusicClient.JoinAsync (БУЛО)

```csharp
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
    // ... ще кілька else if
}
```

**Проблеми:** 4 рівні вкладеності | Складна логіка | Дублювання коду

---

## Приклад: MusicClient.JoinAsync (СТАЛО)

```csharp
public async Task JoinAsync(IVoiceChannel channel)
{
    await Logger.AddLog($"JoinAsync to voice channel {channel.Name} called");
    // Guard clause: якщо клієнта немає, просто підключаємося
    if (audio_client == null) {
        audio_client = await channel.ConnectAsync();
        current_voice_channel = channel;
        return;
    }
    // Guard clause: якщо вже підключені до цього каналу, нічого не робимо
    if (current_voice_channel == channel && 
        audio_client.ConnectionState == ConnectionState.Connected) {
        return;
    }
    // Guard clause: якщо йде підключення або відключення, чекаємо
    if (audio_client.ConnectionState == ConnectionState.Connecting || 
        audio_client.ConnectionState == ConnectionState.Disconnecting) {
        await Task.Delay(CONNECTION_WAIT_MS);
    }
    // Guard clause: якщо підключені, відключаємося перед перепідключенням
    if (audio_client.ConnectionState != ConnectionState.Disconnected) {
        await audio_client.StopAsync();
    }
    // Основна логіка: підключаємося до нового каналу
    audio_client = await channel.ConnectAsync();
    current_voice_channel = channel;
}
```

**Переваги:** ✅ Немає вкладеності | ✅ Лінійна логіка | ✅ Явна обробка випадків


---


<!-- _class: lead -->

# Висновок

**Ключові моменти:**
- Рефакторинг — це покращення структури коду без зміни функціональності
- Три техніки особливо ефективні в C#
- Всі приклади взяті з реального проекту
- Рефакторинг покращує якість коду


