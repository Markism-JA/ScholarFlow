using System.Collections.ObjectModel;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public static MainWindowViewModel DesignInstance { get; } = new MainWindowViewModel();
    public ObservableCollection<Bucket> RecentProjects { get; set; }

    public MainWindowViewModel()
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
}
