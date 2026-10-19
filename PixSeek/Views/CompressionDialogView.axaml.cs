using Avalonia.Controls;

namespace PixSeek.Views;
public partial class CompressionDialogView : UserControl
{
    public CompressionDialogView() => InitializeComponent();
    public void FocusInput()
    {
        var name = DataContext is PixSeek.ViewModels.OptimizerViewModel { IsFolderInput: true } ? "SourceFolderInput" : "OutputFolderInput";
        this.FindControl<TextBox>(name)?.Focus();
    }
}
