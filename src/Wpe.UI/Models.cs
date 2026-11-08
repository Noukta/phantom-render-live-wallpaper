using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Wpe.UI;

/// <summary>A label shown in a combo box with the settings value behind it.</summary>
public sealed record Opt(string Label, string Value)
{
    // The closed combo box shows the selected item's text, so it must be the label.
    public override string ToString() => Label;
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class VideoItem : Observable
{
    public VideoItem(string path) { Path = path; }
    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    BitmapImage? _thumb;
    public BitmapImage? Thumb { get => _thumb; set => Set(ref _thumb, value); }

    string? _playingOn;
    /// <summary>Screens currently showing this video, e.g. "Now playing" or "Screen 1, 2".</summary>
    public string? PlayingOn
    {
        get => _playingOn;
        set { Set(ref _playingOn, value); Raise(nameof(PlayingLabel)); Raise(nameof(PlayingVisibility)); }
    }
    public string PlayingLabel => PlayingOn ?? "";
    public Visibility PlayingVisibility => PlayingOn == null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A playlist entry: a path with a readable name.</summary>
public sealed record PlaylistEntry(string Path)
{
    public string Name => File.Exists(Path) ? System.IO.Path.GetFileNameWithoutExtension(Path) : System.IO.Path.GetFileNameWithoutExtension(Path) + " (missing)";
}
