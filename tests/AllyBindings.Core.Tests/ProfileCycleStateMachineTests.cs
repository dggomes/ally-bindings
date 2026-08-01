using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ProfileCycleStateMachineTests
{
    private static readonly ShortcutSettings Shortcut = new()
    {
        Buttons = [ControllerButton.View, ControllerButton.Menu],
        HoldMilliseconds = 200,
        CommitDelayMilliseconds = 500,
    };

    private static readonly IReadOnlyList<CycleItem> Items =
    [
        CycleItem.ForProfile(MappingProfile.Default),
        CycleItem.ForProfile(new MappingProfile { Id = "elden-ring", Name = "Elden Ring" }),
        CycleItem.OpenApplication,
    ];

    [Fact]
    public void Chord_selects_next_profile_once_and_commits_after_release_timeout()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu);

        Assert.Empty(machine.Process(chord, t0, Items, "default"));
        var selected = machine.Process(chord, t0.AddMilliseconds(200), Items, "default");
        Assert.Single(selected);
        Assert.Equal(CycleEventKind.SelectionChanged, selected[0].Kind);
        Assert.Equal("elden-ring", selected[0].Item?.Id);

        Assert.Empty(machine.Process(chord, t0.AddMilliseconds(400), Items, "default"));
        Assert.Empty(machine.Process(new ControllerSnapshot(true, ControllerButton.None), t0.AddMilliseconds(410), Items, "default"));
        Assert.Empty(machine.Process(new ControllerSnapshot(true, ControllerButton.None), t0.AddMilliseconds(909), Items, "default"));

        var committed = machine.Process(new ControllerSnapshot(true, ControllerButton.None), t0.AddMilliseconds(910), Items, "default");
        Assert.Single(committed);
        Assert.Equal(CycleEventKind.SelectionCommitted, committed[0].Kind);
        Assert.Equal("elden-ring", committed[0].Item?.Id);
    }

    [Fact]
    public void Repeated_press_cycles_to_open_application()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu);
        var released = new ControllerSnapshot(true, ControllerButton.None);

        machine.Process(chord, t0, Items, "default");
        machine.Process(chord, t0.AddMilliseconds(200), Items, "default");
        machine.Process(released, t0.AddMilliseconds(210), Items, "default");
        machine.Process(chord, t0.AddMilliseconds(300), Items, "default");
        var second = machine.Process(chord, t0.AddMilliseconds(500), Items, "default");

        Assert.Equal(CycleItemKind.OpenApplication, Assert.Single(second).Item?.Kind);
    }

    [Fact]
    public void Additional_gameplay_buttons_invalidate_the_shortcut()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var now = DateTimeOffset.UtcNow;
        var chordWithExtraButton = new ControllerSnapshot(
            true,
            ControllerButton.View | ControllerButton.Menu | ControllerButton.A);

        Assert.Empty(machine.Process(chordWithExtraButton, now, Items, "default"));
        Assert.Empty(machine.Process(chordWithExtraButton, now.AddMilliseconds(250), Items, "default"));
    }

    [Fact]
    public void Disconnect_cancels_pending_selection()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu);
        machine.Process(chord, t0, Items, "default");
        machine.Process(chord, t0.AddMilliseconds(200), Items, "default");

        var events = machine.Process(ControllerSnapshot.Disconnected, t0.AddMilliseconds(250), Items, "default");

        Assert.Equal(CycleEventKind.Cancelled, Assert.Single(events).Kind);
        Assert.False(machine.HasPendingSelection);
    }
}
