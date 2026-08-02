namespace AllyBindings.Core;

/// <summary>
/// Keeps controller-first window navigation and background profile cycling mutually exclusive.
/// Entering editor ownership cancels any pending cycle, and cycling stays suppressed until every
/// controller button is released after the editor gives ownership back.
/// </summary>
public sealed class ControllerInputArbitration
{
    private bool _editorOwnedInput;
    private bool _awaitingRelease;

    public ControllerRoutingDecision Route(ControllerSnapshot snapshot, bool editorConsumed)
    {
        if (editorConsumed)
        {
            var enteredEditor = !_editorOwnedInput;
            _editorOwnedInput = true;
            _awaitingRelease = true;
            return new(false, enteredEditor, snapshot with
            {
                Buttons = ControllerButton.None,
                LeftTrigger = 0,
                RightTrigger = 0,
            });
        }

        if (_editorOwnedInput)
        {
            _editorOwnedInput = false;
            _awaitingRelease = true;
        }

        if (_awaitingRelease)
        {
            if (snapshot.Buttons != ControllerButton.None)
            {
                return new(false, true, snapshot with
                {
                    Buttons = ControllerButton.None,
                    LeftTrigger = 0,
                    RightTrigger = 0,
                });
            }
            _awaitingRelease = false;
            return new(true, true, snapshot);
        }

        return new(true, false, snapshot);
    }
}

public readonly record struct ControllerRoutingDecision(
    bool ShouldProcess,
    bool CancelCycle,
    ControllerSnapshot Snapshot);
