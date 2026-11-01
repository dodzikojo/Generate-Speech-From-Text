using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IssueLab.VoiceBench.App.Controls;

internal static class VoiceComboBoxItemFactory
{
    public static ComboBoxItem Create(SpeechVoiceDescriptor voice)
    {
        ArgumentNullException.ThrowIfNull(voice);

        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        content.Children.Add(new TextBlock
        {
            Text = voice.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 82,
        });

        foreach (string tag in voice.Tags)
        {
            content.Children.Add(new Border
            {
                Background = ResourceBrush("DividerBrush"),
                BorderBrush = ResourceBrush("MutedTextBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = tag,
                    FontSize = 11,
                    Foreground = ResourceBrush("MutedTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        ComboBoxItem item = new()
        {
            Content = content,
            Tag = voice.VoiceId,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            item,
            voice.Tags.Count == 0
                ? voice.DisplayName
                : $"{voice.DisplayName}, {string.Join(", ", voice.Tags)}");
        return item;
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];
}
