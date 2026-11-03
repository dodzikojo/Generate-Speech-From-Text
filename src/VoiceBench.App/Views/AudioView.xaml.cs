using IssueLab.VoiceBench.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class AudioView : UserControl
{
    public AudioView() => InitializeComponent();

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out ProjectFolder folder)) return;
        try
        {
            if (!App.Services.Session.HasProject) throw new InvalidOperationException("Open a project first.");
            await ShellLauncher.OpenProjectFolderAsync(folder);
        }
        catch (Exception exception)
        {
            AudioInfoBar.Title = $"{ProjectLocations.DisplayName(folder)} could not be opened";
            AudioInfoBar.Message = exception.Message;
            AudioInfoBar.IsOpen = true;
        }
    }
}
