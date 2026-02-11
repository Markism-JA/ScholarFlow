using System.Threading.Tasks;

namespace ScholarFlow.Services;

public interface IDialogService
{
    Task<string?> OpenFolderAsync(string title, string? defaultPath = null);
}
