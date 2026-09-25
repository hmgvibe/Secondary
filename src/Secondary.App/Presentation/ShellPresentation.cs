using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace Secondary.App.Presentation;
public enum ShellAppearance { Normal = 0, Game = 2 }
public enum ThemeChoice { System, Light, Dark }
public sealed class ShellPresentation : INotifyPropertyChanged
{
    private ShellAppearance appearance;
    public ShellAppearance Appearance { get => appearance; set { appearance = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
