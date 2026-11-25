namespace Kvasir.Media;

public class PCMConvertetionException(string? message = null) : Exception(message ?? "FFmpeg не смог подготовить аудиофайл.");
