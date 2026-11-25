using System.Windows;
using Kvasir.Control;
using Kvasir.Desktop.Controls;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop;

public partial class PlaylistDialog : ThemedDialogWindow
{
    private readonly string? _existingName;

    public string Input => InputBox.Text.Trim();
    public bool Shared => SharedBox.IsChecked == true;

    public PlaylistDialog(string? existingName = null)
    {
        InitializeComponent();
        _existingName = existingName;
        Title = UiText.Get(existingName == null ? "NewPlaylist" : "EditPlaylist");
        InputBox.Text = existingName ?? "";
        SharedBox.Visibility = existingName == null ? Visibility.Visible : Visibility.Collapsed;
        InputBox.TextChanged += (_, _) => UpdateSubmit();
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
        UpdateSubmit();
    }

    private void UpdateSubmit()
    {
        bool isLink = PlaylistInput.GetLink(Input) != null;
        SubmitButton.IsEnabled = Input.Length > 0 && (isLink || Input.Length <= 128) && Input != _existingName;
        string label = "SavePlaylist";
        if (_existingName == null) label = isLink ? "CreateAndImportPlaylist" : "Create";
        else if (isLink) label = "Import";
        SubmitButton.Content = UiText.Get(label);
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (SubmitButton.IsEnabled) DialogResult = true;
    }
}
