using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AllyBindings.Core;

namespace AllyBindings.Windows;

public sealed class BindingRow : INotifyPropertyChanged
{
    private ControllerButton _target;

    public BindingRow(ControllerButton source, ControllerButton target)
    {
        Source = source;
        _target = target;
    }

    public ControllerButton Source { get; }

    public ControllerButton Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Target)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProfileEditor : INotifyPropertyChanged
{
    private string _name;
    private bool _enabled;

    public ProfileEditor(MappingProfile profile)
    {
        Id = profile.Id;
        _name = profile.Name;
        _enabled = profile.Enabled;
        Bindings = new ObservableCollection<BindingRow>(ControllerButtons.Mappable.Select(source =>
            new BindingRow(source, profile.Bindings.GetValueOrDefault(source, source))));
    }

    public string Id { get; private set; }
    public bool IsDefault => Id.Equals(MappingProfile.Default.Id, StringComparison.OrdinalIgnoreCase);

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<BindingRow> Bindings { get; }

    public MappingProfile ToProfile()
    {
        var id = IsDefault ? MappingProfile.Default.Id : ConfigurationValidator.Slugify(Name);
        if (string.IsNullOrWhiteSpace(id)) id = Id;
        Id = id;
        return new MappingProfile
        {
            Id = id,
            Name = IsDefault ? MappingProfile.Default.Name : Name.Trim(),
            Enabled = IsDefault || Enabled,
            Bindings = IsDefault
                ? []
                : Bindings.Where(row => row.Source != row.Target).ToDictionary(row => row.Source, row => row.Target),
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
