using System.IO;
using System.Text.Json;
using System.Windows;
using Kvasir.Configuration;
using Kvasir.Desktop.Controls;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop;

public partial class TokenDialog : ThemedDialogWindow
{
    private readonly BotConfiguration _configuration;
    public TokenDialog(bool rejected = false, BotConfiguration? configuration = null)
    {
        InitializeComponent();
        _configuration = configuration ?? new();
        if (rejected)
        {
            Heading.Text = UiText.Get("TokenInvalidTitle");
            Reason.Text = UiText.Get("RejectedTokenHint");
        }
        Loaded += (_, _) => TokenBox.Focus();
        Closed += (_, _) => TokenBox.Clear();
    }
    private void Token_Changed(object sender, RoutedEventArgs e) => Validation.Text = "";
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _configuration.SaveToken(TokenBox.Password);
            TokenBox.Clear();
            DialogResult = true;
        }
        catch (ArgumentException) { Validation.Text = UiText.Get("InvalidTokenInput"); }
        catch (JsonException) { Validation.Text = UiText.Get("InvalidConfigurationJson"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Validation.Text = UiText.Get("SaveTokenFailed"); }
    }
}
