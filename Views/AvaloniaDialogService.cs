using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using ScholarFlow.Services;

namespace ScholarFlow.Views;

public class AvaloniaDialogService : IDialogService
{
    private TopLevel? GetTopLevel()
    {
        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
        )
        {
            return TopLevel.GetTopLevel(desktop.MainWindow);
        }
        return null;
    }

    public async Task<string?> OpenFolderAsync(string title, string? defaultPath = null)
    {
        var toplevel = GetTopLevel();
        if (toplevel == null)
            return null;

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };

        // If a default path is provided, try to resolve it to an IStorageFolder
        if (!string.IsNullOrEmpty(defaultPath))
        {
            // This converts your string path into an Avalonia-compatible folder object
            options.SuggestedStartLocation =
                await toplevel.StorageProvider.TryGetFolderFromPathAsync(defaultPath);
        }

        var folders = await toplevel.StorageProvider.OpenFolderPickerAsync(options);

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}
