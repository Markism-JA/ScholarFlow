using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScholarFlow.Models.DTOs;
using ScholarFlow.Models.Settings;
using ScholarFlow.Services;

namespace ScholarFlow.ViewModels;

public partial class OnboardingViewModel : ViewModelBase, ISizableViewModel
{
    [ObservableProperty]
    private string _basinPath;
    private readonly IDialogService _dialogService;
    public string BasinName { get; set; }
    public string Username { get; set; }
    public string SchoolEmail { get; set; }
    public string Password { get; set; }
    public ObservableCollection<KnowBasinEntry> RecentProjects { get; set; }

    public OnboardingViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        var newApp = new AppConfig
        {
            KnownBasin =
            [
                new KnowBasinEntry() { Name = "Potrero", FilePath = "~/SomFilePath" },
                new KnowBasinEntry() { Name = "STI", FilePath = "~/SomFilePath" },
            ],
        };

        RecentProjects = LoadBasins(newApp);
    }

    [RelayCommand]
    private async Task SelectFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fallbackPath = Path.Combine(home, "Documents");

        string pathToShow = !string.IsNullOrWhiteSpace(BasinPath) ? BasinPath : fallbackPath;
        var result = await _dialogService.OpenFolderAsync("Select Basin Folder", pathToShow);
        if (result != null)
            BasinPath = result;
    }

    [RelayCommand]
    private async Task OpenBucket()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fallbackPath = Path.Combine(home, "Documents");

        string pathToShow = !string.IsNullOrWhiteSpace(BasinPath) ? BasinPath : fallbackPath;
        var result = await _dialogService.OpenFolderAsync("Select Basin Folder", pathToShow);
        if (result != null)
            throw new NotImplementedException();
    }

    private static ObservableCollection<KnowBasinEntry> LoadBasins(AppConfig config)
    {
        return new ObservableCollection<KnowBasinEntry>(config.KnownBasin);
    }

    public double Width { get; } = 860;
    public double Height { get; } = 720;
    public double MinWidth { get; } = 860;
    public double MinHeight { get; } = 720;
}
