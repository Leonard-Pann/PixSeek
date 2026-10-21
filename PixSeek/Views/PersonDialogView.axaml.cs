using Avalonia.Controls;
using Avalonia.Interactivity;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public partial class PersonDialogView : UserControl
{
    public PersonDialogView() => InitializeComponent();
    private void RemovePerson(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PersonOption person } && DataContext is PeopleViewModel vm) vm.RemoveCommand.Execute(person);
    }
}
