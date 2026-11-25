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

public sealed record BotSelectOption(string Label, string Value, string? Description = null, bool Default = false);

public sealed record BotSelect(
    string Id,
    string Placeholder,
    IReadOnlyCollection<BotSelectOption> Options) : BotComponent;

public sealed record BotText(string Text) : BotComponent;
public sealed record BotSeparator : BotComponent;
public sealed record BotContainer(IReadOnlyCollection<BotComponent> Components, int? AccentColor = null) : BotComponent;

public sealed record BotComponentRow(IReadOnlyCollection<BotComponent> Components) : BotComponent;

public sealed record BotEmbed(
    string Description,
    int Color,
    string AuthorName,
    string? AuthorIconUrl = null);

public sealed record BotMessageContent(
    string Content,
    BotEmbed? Embed = null,
    IReadOnlyCollection<BotComponent>? Components = null,
    bool ComponentsV2 = false);
