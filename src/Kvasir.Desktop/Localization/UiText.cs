using System.Globalization;
using System.Text.Json;
using System.Windows.Markup;
using Kvasir.Control;

namespace Kvasir.Desktop.Localization;

public static class UiText
{
    private static readonly Dictionary<string, string> Russian = Load("ru");
    private static readonly Dictionary<string, string> English = Load("en");

    public static string Language { get; private set; } = "ru";
    private static Dictionary<string, string> Catalog => Language == "en" ? English : Russian;

    public static void SelectLanguage(string language) => Language = language == "en" ? "en" : "ru";

    public static string Get(string key)
    {
        return Catalog.TryGetValue(key, out string? text) ? text : key;
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(Language), Get(key), arguments);

    public static string ResponseMessage(ControlResponse response)
    {
        if (response.MessageKey is not { } key || !Catalog.TryGetValue(key, out string? message)) return response.Message;
        try { return string.Format(CultureInfo.GetCultureInfo(Language), message, response.MessageArguments ?? []); }
        catch (FormatException) { return response.Message; }
    }

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream($"Kvasir.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing localization catalog: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Empty localization catalog: {language}");
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(key);
}
