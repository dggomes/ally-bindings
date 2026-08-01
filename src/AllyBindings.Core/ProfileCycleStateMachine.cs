namespace AllyBindings.Core;

public enum CycleItemKind
{
    Profile,
}

public sealed record CycleItem(CycleItemKind Kind, string Id, string Label)
{
    public static CycleItem ForProfile(MappingProfile profile) => new(CycleItemKind.Profile, profile.Id, profile.Name);
}

public enum CycleEventKind
{
    SelectionChanged,
    SelectionCommitted,
    ApplicationRequested,
    Cancelled,
}

public sealed record CycleEvent(CycleEventKind Kind, CycleItem? Item, string? Message = null);

public sealed class ProfileCycleStateMachine
{
    public const byte RightTriggerConfirmationThreshold = 128;

    private ShortcutSettings _shortcut;
    private bool _wasConnected;
    private bool _chordDown;
    private bool _firedForPress;
    private bool _applicationActivationArmed;
    private bool _rightTriggerWasDown;
    private bool _invalidUntilChordReleased;
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
        var requiredMask = _shortcut.Buttons.Aggregate(
            ControllerButton.None,
            static (mask, button) => mask | button);
        var pressedStandardButtons = snapshot.Buttons & ControllerButtons.ShortcutMask;
        var allChordButtonsHeld = (pressedStandardButtons & requiredMask) == requiredMask;
        var hasExtraStandardButton =
            allChordButtonsHeld && (pressedStandardButtons & ~requiredMask) != ControllerButton.None;
        var isDown = snapshot.Buttons.IsExactChord(_shortcut.Buttons);
        var isRightTriggerDown = snapshot.RightTrigger >= RightTriggerConfirmationThreshold;

        if (_invalidUntilChordReleased)
        {
            _rightTriggerWasDown = isRightTriggerDown;
            if ((pressedStandardButtons & requiredMask) == ControllerButton.None)
            {
                _invalidUntilChordReleased = false;
            }
            return events;
        }

        if (hasExtraStandardButton ||
            (HasPendingSelection &&
             !isDown &&
             (pressedStandardButtons & ~requiredMask) != ControllerButton.None))
        {
            if (HasPendingSelection)
            {
                events.Add(new(CycleEventKind.Cancelled, PendingSelection, "Additional controller input cancelled the selection."));
            }
            _invalidUntilChordReleased = allChordButtonsHeld;
            _chordDown = false;
            _firedForPress = false;
            _applicationActivationArmed = false;
            _pressedAt = null;
            _rightTriggerWasDown = isRightTriggerDown;
            ResetSelection();
            return events;
        }

        if (isDown && !_chordDown)
        {
            _chordDown = true;
            _firedForPress = false;
            _applicationActivationArmed = false;
            // RT must be pressed after the chord is armed. Carrying a gameplay
            // trigger into the shortcut can never open the application.
            _rightTriggerWasDown = isRightTriggerDown;
            _pressedAt = now;
        }

        if (isDown && !_firedForPress && _pressedAt.HasValue &&
            now - _pressedAt.Value >= TimeSpan.FromMilliseconds(_shortcut.HoldMilliseconds))
        {
            _firedForPress = true;
            _applicationActivationArmed = true;
            _lastInteractionAt = now;

            // If RT crosses the threshold on the exact sample that arms the
            // chord, opening the editor wins. Never emit a transient profile
            // selection for the same physical gesture.
            if (isRightTriggerDown && !_rightTriggerWasDown)
            {
                ResetSelection();
                _applicationActivationArmed = false;
                events.Add(new(CycleEventKind.ApplicationRequested, null));
            }
            else
            {
                _items = availableItems;
                if (_items.Count == 0)
                {
                    Reset();
                    return events;
                }
                if (_selectedIndex < 0)
                {
                    var activeIndex = FindProfileIndex(_items, activeProfileId);
                    _selectedIndex = (activeIndex + 1 + _items.Count) % _items.Count;
                }
                else
                {
                    _selectedIndex = (_selectedIndex + 1) % _items.Count;
                }

                events.Add(new(CycleEventKind.SelectionChanged, PendingSelection));
            }
        }

        if (_chordDown && _applicationActivationArmed && isRightTriggerDown && !_rightTriggerWasDown)
        {
            ResetSelection();
            _applicationActivationArmed = false;
            events.Add(new(CycleEventKind.ApplicationRequested, null));
        }

        _rightTriggerWasDown = isRightTriggerDown;

        if (!isDown && _chordDown)
        {
            _chordDown = false;
            _applicationActivationArmed = false;
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
        _applicationActivationArmed = false;
        _rightTriggerWasDown = false;
        _invalidUntilChordReleased = false;
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
