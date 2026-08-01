namespace AllyBindings.Core;

public enum CycleItemKind
{
    Profile,
    OpenApplication,
}

public sealed record CycleItem(CycleItemKind Kind, string Id, string Label)
{
    public static CycleItem ForProfile(MappingProfile profile) => new(CycleItemKind.Profile, profile.Id, profile.Name);
    public static CycleItem OpenApplication { get; } = new(CycleItemKind.OpenApplication, "open-application", "Open application");
}

public enum CycleEventKind
{
    SelectionChanged,
    SelectionCommitted,
    Cancelled,
}

public sealed record CycleEvent(CycleEventKind Kind, CycleItem? Item, string? Message = null);

public sealed class ProfileCycleStateMachine
{
    private ShortcutSettings _shortcut;
    private bool _wasConnected;
    private bool _chordDown;
    private bool _firedForPress;
    private DateTimeOffset? _pressedAt;
    private DateTimeOffset? _lastInteractionAt;
    private IReadOnlyList<CycleItem> _items = [];
    private int _selectedIndex = -1;

    public ProfileCycleStateMachine(ShortcutSettings shortcut)
    {
        _shortcut = ConfigurationValidator.Normalize(new AppConfiguration { Shortcut = shortcut }).Configuration.Shortcut;
    }

    public bool HasPendingSelection => _selectedIndex >= 0;
    public CycleItem? PendingSelection => HasPendingSelection ? _items[_selectedIndex] : null;

    public void UpdateShortcut(ShortcutSettings shortcut)
    {
        _shortcut = ConfigurationValidator.Normalize(new AppConfiguration { Shortcut = shortcut }).Configuration.Shortcut;
        Reset();
    }

    public IReadOnlyList<CycleEvent> Process(
        ControllerSnapshot snapshot,
        DateTimeOffset now,
        IReadOnlyList<CycleItem> availableItems,
        string activeProfileId)
    {
        var events = new List<CycleEvent>();
        if (!snapshot.IsConnected)
        {
            if (_wasConnected && HasPendingSelection)
            {
                events.Add(new(CycleEventKind.Cancelled, PendingSelection, "Controller disconnected; selection cancelled."));
            }
            _wasConnected = false;
            Reset();
            return events;
        }

        _wasConnected = true;
        var isDown = snapshot.Buttons.IsExactChord(_shortcut.Buttons);
        if (isDown && !_chordDown)
        {
            _chordDown = true;
            _firedForPress = false;
            _pressedAt = now;
        }

        if (isDown && !_firedForPress && _pressedAt.HasValue &&
            now - _pressedAt.Value >= TimeSpan.FromMilliseconds(_shortcut.HoldMilliseconds))
        {
            _items = availableItems.Count > 0 ? availableItems : [CycleItem.OpenApplication];
            if (_selectedIndex < 0)
            {
                var activeIndex = FindProfileIndex(_items, activeProfileId);
                _selectedIndex = (activeIndex + 1 + _items.Count) % _items.Count;
            }
            else
            {
                _selectedIndex = (_selectedIndex + 1) % _items.Count;
            }

            _firedForPress = true;
            _lastInteractionAt = now;
            events.Add(new(CycleEventKind.SelectionChanged, PendingSelection));
        }

        if (!isDown && _chordDown)
        {
            _chordDown = false;
            _pressedAt = null;
            if (_firedForPress)
            {
                _lastInteractionAt = now;
            }
            _firedForPress = false;
        }

        if (!_chordDown && HasPendingSelection && _lastInteractionAt.HasValue &&
            now - _lastInteractionAt.Value >= TimeSpan.FromMilliseconds(_shortcut.CommitDelayMilliseconds))
        {
            var committed = PendingSelection;
            events.Add(new(CycleEventKind.SelectionCommitted, committed));
            ResetSelection();
        }

        return events;
    }

    public void Cancel()
    {
        Reset();
    }

    private static int FindProfileIndex(IReadOnlyList<CycleItem> items, string activeProfileId)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind == CycleItemKind.Profile &&
                items[i].Id.Equals(activeProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private void Reset()
    {
        _chordDown = false;
        _firedForPress = false;
        _pressedAt = null;
        ResetSelection();
    }

    private void ResetSelection()
    {
        _lastInteractionAt = null;
        _items = [];
        _selectedIndex = -1;
    }
}
