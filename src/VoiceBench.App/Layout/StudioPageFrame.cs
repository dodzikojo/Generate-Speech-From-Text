using Microsoft.UI.Xaml;

namespace IssueLab.VoiceBench.App.Layout;

internal static class StudioPageFrame
{
    public static void Resize(
        FrameworkElement frame,
        double availableWidth,
        double maxContentWidth,
        double horizontalGutter)
    {
        double usableWidth = Math.Max(0, availableWidth - (horizontalGutter * 2));
        frame.Width = Math.Min(maxContentWidth, usableWidth);
    }
}
