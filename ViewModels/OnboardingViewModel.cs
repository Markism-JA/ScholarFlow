using System.Collections.ObjectModel;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.ViewModels;

public class OnboardingViewModel : ViewModelBase, ISizableViewModel
{
    public static OnboardingViewModel DesignInstance { get; } = new OnboardingViewModel();
    public ObservableCollection<Bucket> RecentProjects { get; set; }

    public OnboardingViewModel()
    {
        var newApp = new AppConfig
        {
            KnownBasin =
            [
                new Bucket { Name = "Potrero", FilePath = "~/SomFilePath" },
                new Bucket { Name = "STI", FilePath = "~/SomFilePath" },
            ],
        };

        RecentProjects = LoadBasins(newApp);
    }

    private static ObservableCollection<Bucket> LoadBasins(AppConfig config)
    {
        return new ObservableCollection<Bucket>(config.KnownBasin);
    }

    public double Width { get; } = 860;
    public double Height { get; } = 720;
    public double MinWidth { get; } = 860;
    public double MinHeight { get; } = 720;
}
