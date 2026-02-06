using System.Collections.ObjectModel;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<Bucket> RecentProjects { get; set; }

    public MainWindowViewModel()
    {
        RecentProjects = new ObservableCollection<Bucket>
        {
            new Bucket
            {
                Name = "STI",
                FilePath = "~/somePath",
                LastModified = "2 Hours Ago",
            },
            new Bucket
            {
                Name = "Potrero",
                FilePath = "~/somePath",
                LastModified = "Some time ago",
            },
        };
    }
}
