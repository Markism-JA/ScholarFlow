using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using HotAvalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScholarFlow.Data;
using ScholarFlow.Services;
using ScholarFlow.ViewModels;
using ScholarFlow.Views;

namespace ScholarFlow;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; }

    public override void Initialize()
    {
        this.EnableHotReload();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite("Data Source=scholarflow.db")
        );
        services.AddTransient<MainWindowViewModel>();

        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddTransient<OnboardingViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Resolve the ViewModel from the container (this includes all injected services)
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();

            DisableAvaloniaDataAnnotationValidation();

            // USE the resolved viewModel instead of 'new MainWindowViewModel()'
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove = BindingPlugins
            .DataValidators.OfType<DataAnnotationsValidationPlugin>()
            .ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
