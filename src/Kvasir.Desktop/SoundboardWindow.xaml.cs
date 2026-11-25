using System.Windows;

namespace Kvasir.Desktop;

public partial class SoundboardWindow : Window
{
    public SoundboardWindow() => InitializeComponent();
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
