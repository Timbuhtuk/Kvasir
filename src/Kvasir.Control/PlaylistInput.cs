namespace Kvasir.Control;

public static class PlaylistInput
{
    public static string? GetLink(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return input;

        if (input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = input.Split(':');
            if (parts.Length == 3 && parts[1] is "playlist" or "album" && parts[2].Length > 0 &&
                parts[2].All(char.IsAsciiLetterOrDigit))
                return $"https://open.spotify.com/{parts[1]}/{parts[2]}";
        }
        return null;
    }
}
