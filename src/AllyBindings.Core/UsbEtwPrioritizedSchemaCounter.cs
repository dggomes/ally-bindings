namespace AllyBindings.Core;

/// <summary>
/// Maintains independent bounded cardinality budgets for high-priority transfer
/// metadata and lower-priority framing metadata. A full framing partition can
/// never consume capacity reserved for a later transfer-data/status shape.
/// </summary>
internal sealed class UsbEtwPrioritizedSchemaCounter<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, long> priority = [];
    private readonly Dictionary<TKey, long> framing = [];
    private readonly Func<TKey, int> getPhase;
    private readonly int maximumPriorityKeys;
    private readonly int maximumPriorityKeysPerPhase;
    private readonly int maximumFramingKeys;
    private readonly int maximumFramingKeysPerPhase;

    public UsbEtwPrioritizedSchemaCounter(
        Func<TKey, int> getPhase,
        int maximumPriorityKeys,
        int maximumPriorityKeysPerPhase,
        int maximumFramingKeys,
        int maximumFramingKeysPerPhase)
    {
        ArgumentNullException.ThrowIfNull(getPhase);
        if (maximumPriorityKeys <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPriorityKeys));
        if (maximumPriorityKeysPerPhase <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPriorityKeysPerPhase));
        if (maximumFramingKeys <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFramingKeys));
        if (maximumFramingKeysPerPhase <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFramingKeysPerPhase));
        this.getPhase = getPhase;
        this.maximumPriorityKeys = maximumPriorityKeys;
        this.maximumPriorityKeysPerPhase = maximumPriorityKeysPerPhase;
        this.maximumFramingKeys = maximumFramingKeys;
        this.maximumFramingKeysPerPhase = maximumFramingKeysPerPhase;
    }

    public IEnumerable<KeyValuePair<TKey, long>> Entries => priority.Concat(framing);

    public bool Increment(TKey key, UsbEtwSchemaRetentionClass retentionClass)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (target, maximumKeys, maximumKeysPerPhase) = retentionClass switch
        {
            UsbEtwSchemaRetentionClass.Priority =>
                (priority, maximumPriorityKeys, maximumPriorityKeysPerPhase),
            UsbEtwSchemaRetentionClass.Framing =>
                (framing, maximumFramingKeys, maximumFramingKeysPerPhase),
            _ => throw new ArgumentOutOfRangeException(nameof(retentionClass)),
        };

        if (target.TryGetValue(key, out var count))
        {
            target[key] = count == long.MaxValue ? long.MaxValue : count + 1;
            return true;
        }

        var phase = getPhase(key);
        if (target.Count >= maximumKeys ||
            target.Keys.Count(existing => getPhase(existing) == phase) >= maximumKeysPerPhase)
        {
            return false;
        }

        target.Add(key, 1);
        return true;
    }
}
