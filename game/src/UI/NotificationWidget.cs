using AlleyCat.Common;
using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Displays queued, temporary notifications in the global UI overlay.
/// </summary>
[GlobalClass]
public partial class NotificationWidget : MarginContainer, INotificationWidget
{
    private readonly List<NotificationEntry> _entries = [];
    private VBoxContainer? _messagesContainer;
    private ulong _nextEntryId;

    /// <inheritdoc />
    [Export(PropertyHint.Range, "1,100,1")]
    public int MaximumQueueSize
    {
        get;
        set
        {
            field = Math.Max(1, value);
            TrimToCapacity();
            UpdateWidgetVisibility();
        }
    } = 10;

    /// <inheritdoc />
    public override void _Ready()
    {
        EnsureContainerBound();
        ClearNotifications();
    }

    /// <inheritdoc />
    public void PostNotification(string message, double timeoutSeconds = 3.0)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        EnsureContainerBound();

        Label label = CreateNotificationLabel(message);
        _messagesContainer!.AddChild(label);
        _messagesContainer.MoveChild(label, 0);

        ulong entryId = ++_nextEntryId;
        _entries.Insert(0, new NotificationEntry(label, entryId));
        ScheduleExpiry(entryId, timeoutSeconds);

        TrimToCapacity();
        UpdateWidgetVisibility();
    }

    /// <inheritdoc />
    public void ClearNotifications()
    {
        EnsureContainerBound();

        foreach (NotificationEntry entry in _entries)
        {
            entry.Label.QueueFree();
        }

        _entries.Clear();
        UpdateWidgetVisibility();
    }

    private static Label CreateNotificationLabel(string message)
        => new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = message,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

    private void EnsureContainerBound()
        => _messagesContainer ??= this.RequireNode<VBoxContainer>("Messages");

    private void ScheduleExpiry(ulong entryId, double timeoutSeconds)
    {
        if (timeoutSeconds <= 0.0)
        {
            ExpireEntry(entryId);
            return;
        }

        SceneTree? sceneTree = GetTree();
        if (sceneTree is null)
        {
            return;
        }

        SceneTreeTimer timer = sceneTree.CreateTimer(timeoutSeconds, processAlways: true);
        AwaitTimerExpiryAsync(sceneTree, timer, entryId);
    }

    private void TrimToCapacity()
    {
        while (_entries.Count > MaximumQueueSize)
        {
            NotificationEntry entry = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            entry.Label.QueueFree();
        }
    }

    private void UpdateWidgetVisibility()
    {
        if (_entries.Count > 0)
        {
            Show();
            return;
        }

        Hide();
    }

    private async void AwaitTimerExpiryAsync(SceneTree sceneTree, SceneTreeTimer timer, ulong entryId)
    {
        _ = await sceneTree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);

        if (!IsInsideTree())
        {
            return;
        }

        ExpireEntry(entryId);
    }

    private void ExpireEntry(ulong entryId)
    {
        int index = _entries.FindIndex(entry => entry.EntryId == entryId);
        if (index < 0)
        {
            return;
        }

        NotificationEntry entry = _entries[index];
        _entries.RemoveAt(index);
        entry.Label.QueueFree();
        UpdateWidgetVisibility();
    }

    private sealed class NotificationEntry(Label label, ulong entryId)
    {
        public Label Label { get; } = label;

        public ulong EntryId { get; } = entryId;
    }
}
