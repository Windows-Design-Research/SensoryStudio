using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace SensoryStudio.WinUI.Services;

public static class SessionCompletionDialog
{
    private static readonly ResourceLoader Resources = new();

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = Resources.GetString("SessionCompleteDialogTitle"),
            Content = Resources.GetString("SessionCompleteDialogMessage"),
            PrimaryButtonText = Resources.GetString("SessionCompleteDialogOkButton"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        await dialog.ShowAsync();
    }
}