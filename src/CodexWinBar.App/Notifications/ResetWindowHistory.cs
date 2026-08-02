using CodexWinBar.Core.Providers;

namespace CodexWinBar.App.Notifications;

/// <summary>
/// Keeps bounded notification history for the currently observed reset window in each provider slot.
/// Replacing a reset window drops every event belonging to the obsolete window.
/// </summary>
internal sealed class ResetWindowHistory<T> where T : struct
{
    private readonly int maxFiredKeys;
    private readonly Dictionary<NotificationSlot, WindowEntry> current = [];
    private readonly HashSet<FiredEvent> fired = [];
    private readonly Queue<FiredEvent> firedOrder = [];

    public ResetWindowHistory(int maxFiredKeys)
    {
        this.maxFiredKeys = maxFiredKeys;
    }

    internal ResetObservation<T> Observe(NotificationSlot slot, string resetKey, T value)
    {
        if (!this.current.TryGetValue(slot, out var prior))
        {
            this.current[slot] = new WindowEntry(resetKey, value, WasNotified: false);
            return new ResetObservation<T>(HadPrevious: false, Previous: default, ResetChanged: false, PreviousWasNotified: false);
        }

        if (string.Equals(prior.ResetKey, resetKey, StringComparison.Ordinal))
        {
            this.current[slot] = prior with { Value = value };
            return new ResetObservation<T>(HadPrevious: true, prior.Value, ResetChanged: false, PreviousWasNotified: false);
        }

        this.PruneEvents(slot, prior.ResetKey);
        this.current[slot] = new WindowEntry(resetKey, value, WasNotified: false);
        return new ResetObservation<T>(HadPrevious: false, Previous: default, ResetChanged: true, prior.WasNotified);
    }

    internal bool TryMarkFired(NotificationSlot slot, string resetKey, string eventName, bool marksWindowNotified = false)
    {
        var key = new FiredEvent(slot, resetKey, eventName);
        if (!this.fired.Add(key))
        {
            return false;
        }

        this.firedOrder.Enqueue(key);
        if (marksWindowNotified && this.current.TryGetValue(slot, out var entry) &&
            string.Equals(entry.ResetKey, resetKey, StringComparison.Ordinal))
        {
            this.current[slot] = entry with { WasNotified = true };
        }

        while (this.firedOrder.Count > this.maxFiredKeys)
        {
            _ = this.fired.Remove(this.firedOrder.Dequeue());
        }

        return true;
    }

    internal void ClearWindowNotification(NotificationSlot slot, string resetKey)
    {
        if (this.current.TryGetValue(slot, out var entry) &&
            string.Equals(entry.ResetKey, resetKey, StringComparison.Ordinal))
        {
            this.current[slot] = entry with { WasNotified = false };
        }
    }

    internal void RetainOnly(IReadOnlySet<NotificationSlot> activeSlots)
    {
        foreach (var slot in this.current.Keys.Where(slot => !activeSlots.Contains(slot)).ToArray())
        {
            this.Forget(slot);
        }
    }

    internal void Forget(NotificationSlot slot)
    {
        if (this.current.Remove(slot, out var prior))
        {
            this.PruneEvents(slot, prior.ResetKey);
        }
    }

    internal void Clear()
    {
        this.current.Clear();
        this.fired.Clear();
        this.firedOrder.Clear();
    }

    internal int ActiveWindowCount => this.current.Count;

    internal int FiredEventCount => this.fired.Count;

    private void PruneEvents(NotificationSlot slot, string resetKey)
    {
        var count = this.firedOrder.Count;
        for (var index = 0; index < count; index++)
        {
            var key = this.firedOrder.Dequeue();
            if (key.Slot == slot && string.Equals(key.ResetKey, resetKey, StringComparison.Ordinal))
            {
                _ = this.fired.Remove(key);
            }
            else
            {
                this.firedOrder.Enqueue(key);
            }
        }
    }

    private readonly record struct WindowEntry(string ResetKey, T Value, bool WasNotified);

    private readonly record struct FiredEvent(NotificationSlot Slot, string ResetKey, string EventName);
}

internal readonly record struct NotificationSlot(ProviderId Provider, string Slot);

internal readonly record struct ResetObservation<T>(bool HadPrevious, T Previous, bool ResetChanged, bool PreviousWasNotified)
    where T : struct;
