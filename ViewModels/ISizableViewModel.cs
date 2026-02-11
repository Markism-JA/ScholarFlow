namespace ScholarFlow.ViewModels;

public interface ISizableViewModel
{
    double Width { get; }
    double Height { get; }
    double MinWidth { get; }
    double MinHeight { get; }
}
