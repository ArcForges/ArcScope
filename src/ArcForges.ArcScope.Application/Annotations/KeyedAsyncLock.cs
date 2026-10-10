// SPDX-License-Identifier: AGPL-3.0-only
namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// Serializes work per key (TH-05: each annotation edit protects its write ordering with an async lock). Different keys
/// proceed in parallel; an idle key holds no state.
/// </summary>
internal sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Gate> gates = [];
    private readonly Lock sync = new();

    /// <summary>Waits for exclusive ownership of <paramref name="key"/>; cancellation while waiting acquires nothing.</summary>
    public async ValueTask<Releaser> AcquireAsync(TKey key, CancellationToken cancellationToken)
    {
        Gate gate;
        lock (sync)
        {
            if (!gates.TryGetValue(key, out var existing))
            {
                existing = new Gate();
                gates.Add(key, existing);
            }

            gate = existing;

            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, gate, entered: false);
            throw;
        }

        return new Releaser(this, key, gate);
    }

    /// <summary>The number of keys that currently have an owner or waiters (diagnostic for tests).</summary>
    internal int ActiveKeys
    {
        get
        {
            lock (sync) return gates.Count;
        }
    }

    private void Release(TKey key, Gate gate, bool entered)
    {
        if (entered) gate.Semaphore.Release();
        lock (sync)
        {
            if (--gate.References == 0)
            {
                gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    internal readonly struct Releaser : IDisposable
    {
        private readonly KeyedAsyncLock<TKey>? owner;
        private readonly TKey key;
        private readonly Gate? gate;

        internal Releaser(KeyedAsyncLock<TKey> owner, TKey key, Gate gate)
        {
            this.owner = owner;
            this.key = key;
            this.gate = gate;
        }

        public void Dispose() => owner?.Release(key, gate!, entered: true);
    }

    internal sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }
}
