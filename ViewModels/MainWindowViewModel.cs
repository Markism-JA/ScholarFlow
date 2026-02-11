using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ScholarFlow.Services;

namespace ScholarFlow.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, ISizableViewModel
{
    [ObservableProperty]
    private string _windowTitle = "ScholarFlow";

    [ObservableProperty]
    private ViewModelBase _currentPage;

    public MainWindowViewModel(IDialogService dialogService)
    {
        var startupPage = new OnboardingViewModel(dialogService);
        _currentPage = startupPage;
        ApplyDimension(startupPage);
    }

    private void ApplyDimension(ViewModelBase page)
    {
        if (page is ISizableViewModel sizable)
        {
            Width = sizable.Width;
            Height = sizable.Height;
            MinWidth = sizable.MinWidth;
            MinHeight = sizable.MinHeight;
        }
    }

    public double Width { get; set; }
    public double Height { get; set; }
    public double MinWidth { get; set; }
    public double MinHeight { get; set; }
}
