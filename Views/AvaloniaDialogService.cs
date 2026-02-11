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

        if (!string.IsNullOrEmpty(defaultPath))
        {
            options.SuggestedStartLocation =
                await toplevel.StorageProvider.TryGetFolderFromPathAsync(defaultPath);
        }

        var folders = await toplevel.StorageProvider.OpenFolderPickerAsync(options);

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}
