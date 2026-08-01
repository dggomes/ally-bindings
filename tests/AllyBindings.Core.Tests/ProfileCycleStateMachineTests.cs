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
    public void Repeated_press_rotates_only_through_profiles()
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

        Assert.Equal("default", Assert.Single(second).Item?.Id);
    }

    [Fact]
    public void Armed_chord_then_new_rt_press_requests_application_and_cancels_selection()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = ControllerButton.View | ControllerButton.Menu;

        machine.Process(new ControllerSnapshot(true, chord), t0, Items, "default");
        machine.Process(new ControllerSnapshot(true, chord), t0.AddMilliseconds(200), Items, "default");
        var opened = machine.Process(
            new ControllerSnapshot(true, chord, RightTrigger: ProfileCycleStateMachine.RightTriggerConfirmationThreshold),
            t0.AddMilliseconds(220),
            Items,
            "default");

        Assert.Equal(CycleEventKind.ApplicationRequested, Assert.Single(opened).Kind);
        Assert.False(machine.HasPendingSelection);

        machine.Process(new ControllerSnapshot(true, ControllerButton.None), t0.AddMilliseconds(230), Items, "default");
        Assert.Empty(machine.Process(new ControllerSnapshot(true, ControllerButton.None), t0.AddMilliseconds(800), Items, "default"));
    }

    [Fact]
    public void Rt_held_before_chord_arming_does_not_open_until_released_and_pressed_again()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = ControllerButton.View | ControllerButton.Menu;
        var rt = ProfileCycleStateMachine.RightTriggerConfirmationThreshold;

        Assert.Empty(machine.Process(new ControllerSnapshot(true, chord, RightTrigger: rt), t0, Items, "default"));
        var selected = machine.Process(new ControllerSnapshot(true, chord, RightTrigger: rt), t0.AddMilliseconds(200), Items, "default");
        Assert.Equal(CycleEventKind.SelectionChanged, Assert.Single(selected).Kind);
        Assert.Empty(machine.Process(new ControllerSnapshot(true, chord, RightTrigger: rt), t0.AddMilliseconds(220), Items, "default"));
        Assert.Empty(machine.Process(new ControllerSnapshot(true, chord), t0.AddMilliseconds(240), Items, "default"));

        var opened = machine.Process(new ControllerSnapshot(true, chord, RightTrigger: rt), t0.AddMilliseconds(260), Items, "default");
        Assert.Equal(CycleEventKind.ApplicationRequested, Assert.Single(opened).Kind);
    }

    [Fact]
    public void Rt_edge_on_the_arming_sample_opens_without_selection_flicker()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = ControllerButton.View | ControllerButton.Menu;

        machine.Process(new ControllerSnapshot(true, chord), t0, Items, "default");
        var events = machine.Process(
            new ControllerSnapshot(
                true,
                chord,
                RightTrigger: ProfileCycleStateMachine.RightTriggerConfirmationThreshold),
            t0.AddMilliseconds(200),
            Items,
            "default");

        var requested = Assert.Single(events);
        Assert.Equal(CycleEventKind.ApplicationRequested, requested.Kind);
        Assert.False(machine.HasPendingSelection);
    }

    [Fact]
    public void Rt_without_an_armed_chord_never_requests_application()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var rtOnly = new ControllerSnapshot(
            true,
            ControllerButton.None,
            RightTrigger: ProfileCycleStateMachine.RightTriggerConfirmationThreshold);

        Assert.Empty(machine.Process(rtOnly, t0, Items, "default"));
        Assert.Empty(machine.Process(rtOnly, t0.AddMilliseconds(500), Items, "default"));
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
    public void Additional_button_after_selection_cancels_instead_of_committing()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = ControllerButton.View | ControllerButton.Menu;
        machine.Process(new ControllerSnapshot(true, chord), t0, Items, "default");
        machine.Process(new ControllerSnapshot(true, chord), t0.AddMilliseconds(200), Items, "default");

        var cancelled = machine.Process(
            new ControllerSnapshot(true, chord | ControllerButton.A),
            t0.AddMilliseconds(220),
            Items,
            "default");

        Assert.Equal(CycleEventKind.Cancelled, Assert.Single(cancelled).Kind);
        Assert.False(machine.HasPendingSelection);
        Assert.Empty(machine.Process(
            new ControllerSnapshot(true, ControllerButton.None),
            t0.AddMilliseconds(800),
            Items,
            "default"));
    }

    [Fact]
    public void Staggered_release_of_the_shortcut_buttons_still_commits()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = ControllerButton.View | ControllerButton.Menu;
        machine.Process(new ControllerSnapshot(true, chord), t0, Items, "default");
        machine.Process(new ControllerSnapshot(true, chord), t0.AddMilliseconds(200), Items, "default");

        Assert.Empty(machine.Process(
            new ControllerSnapshot(true, ControllerButton.View),
            t0.AddMilliseconds(220),
            Items,
            "default"));
        Assert.Empty(machine.Process(
            new ControllerSnapshot(true, ControllerButton.None),
            t0.AddMilliseconds(240),
            Items,
            "default"));

        var committed = machine.Process(
            new ControllerSnapshot(true, ControllerButton.None),
            t0.AddMilliseconds(740),
            Items,
            "default");
        Assert.Equal(CycleEventKind.SelectionCommitted, Assert.Single(committed).Kind);
    }

    [Fact]
    public void Removing_extra_button_while_chord_remains_held_does_not_rearm()
    {
        var machine = new ProfileCycleStateMachine(Shortcut);
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var chord = ControllerButton.View | ControllerButton.Menu;

        Assert.Empty(machine.Process(
            new ControllerSnapshot(true, chord | ControllerButton.A),
            t0,
            Items,
            "default"));
        Assert.Empty(machine.Process(
            new ControllerSnapshot(true, chord),
            t0.AddMilliseconds(250),
            Items,
            "default"));
        Assert.Empty(machine.Process(
            new ControllerSnapshot(true, chord),
            t0.AddMilliseconds(500),
            Items,
            "default"));

        machine.Process(new ControllerSnapshot(true, ControllerButton.None), t0.AddMilliseconds(510), Items, "default");
        machine.Process(new ControllerSnapshot(true, chord), t0.AddMilliseconds(520), Items, "default");
        var selected = machine.Process(new ControllerSnapshot(true, chord), t0.AddMilliseconds(720), Items, "default");
        Assert.Equal(CycleEventKind.SelectionChanged, Assert.Single(selected).Kind);
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
