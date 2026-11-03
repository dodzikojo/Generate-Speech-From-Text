using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Controls;

public sealed partial class AudioTimelineControl
{
    private void Asset_RightTapped(object sender,RightTappedRoutedEventArgs e)
    {
        DependencyObject? item=e.OriginalSource as DependencyObject;
        while(item is not null && item!=AssetsList)
        {
            if(item is FrameworkElement {DataContext:AudioAssetRecord asset}){AssetsList.SelectedItem=asset;break;}
            item=VisualTreeHelper.GetParent(item);
        }
    }

    private async void RevealAsset_Click(object sender,RoutedEventArgs e)=>await RunAsync(async()=>
    {
        var asset=AssetsList.SelectedItem as AudioAssetRecord??throw new InvalidOperationException("Select an asset first.");
        await App.Services.RunTimelineCommandAsync<object>("file.reveal",new {relativePath=asset.RelativePath});
    });

    private async void RelinkAsset_Click(object sender,RoutedEventArgs e)=>await RunAsync(async()=>
    {
        var asset=AssetsList.SelectedItem as AudioAssetRecord??throw new InvalidOperationException("Select an asset first.");
        FileOpenPicker picker=new(){SuggestedStartLocation=PickerLocationId.MusicLibrary};
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file=await picker.PickSingleFileAsync();
        if(file is null)return;
        if(string.Equals(Path.GetFullPath(file.Path),Path.GetFullPath(App.Services.Session.ResolveProjectPath(asset.RelativePath)),StringComparison.OrdinalIgnoreCase))
        {Workspace.SetNotice("This is already the registered source. Choose a separate matching original to restore it.");return;}
        ContentDialog confirm=new(){XamlRoot=XamlRoot,Title="Restore original source",PrimaryButtonText="Restore matching original",CloseButtonText="Cancel",
            Content=new TextBlock {Text=$"Restore {asset.Name} from {file.Name}? VoiceBench verifies that every byte matches the registered original. Different audio must be imported as a new asset; existing clips stay pinned.",TextWrapping=TextWrapping.Wrap}};
        if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;
        await App.Services.RunTrackedJobAsync<object>("assets.relink",new {assetId=asset.AssetId,sourcePath=file.Path});
        Workspace.SetNotice("Original source restored with its verified hash. Existing clips remain unchanged.");
        await Workspace.LoadAsync();
    });
}
