namespace Application.Models;

public enum BotButtonStyle
{
    Primary,
    Secondary,
    Success,
    Danger,
}

public abstract record BotComponent;

public sealed record BotButton(
    string Id,
    string Label,
    BotButtonStyle Style,
    bool Disabled = false) : BotComponent;

public sealed record BotSelectOption(string Label, string Value);

public sealed record BotSelect(
    string Id,
    string Placeholder,
    IReadOnlyCollection<BotSelectOption> Options) : BotComponent;

public sealed record BotComponentRow(IReadOnlyCollection<BotComponent> Components) : BotComponent;

public sealed record BotEmbed(
    string Description,
    int Color,
    string AuthorName,
    string? AuthorIconUrl = null);

public sealed record BotMessageContent(
    string Content,
    BotEmbed? Embed = null,
    IReadOnlyCollection<BotComponent>? Components = null);
