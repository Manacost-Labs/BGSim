namespace BGSim.Server;

/// <summary>
/// The global limit on simulations: at most <c>slots</c> run at once (one thread each) and at most <c>queue</c> more wait
/// for a slot; a request beyond that, or one that waited too long, is turned away at once (503) instead of piling up.
/// </summary>
internal sealed class SimulationGate(int slots, int queue, TimeSpan queueTimeout) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(slots, slots);
    private int _admitted;

    /// <summary>Requests running or waiting now.</summary>
    public int Admitted => Volatile.Read(ref _admitted);

    /// <summary>A slot, or null when the server is full (no slot and the queue is full, or the wait timed out).</summary>
    public async ValueTask<Lease?> EnterAsync(CancellationToken cancel)
    {
        if (Interlocked.Increment(ref _admitted) > slots + queue)
        {
            Interlocked.Decrement(ref _admitted);
            return null;
        }

        bool entered;
        try
        {
            entered = await _slots.WaitAsync(queueTimeout, cancel).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _admitted);
            throw;
        }

        if (!entered)
        {
            Interlocked.Decrement(ref _admitted);
            return null;
        }

        return new Lease(this);
    }

    public void Dispose() => _slots.Dispose();

    private void Exit()
    {
        _slots.Release();
        Interlocked.Decrement(ref _admitted);
    }

    /// <summary>A held slot; disposing gives it back (once).</summary>
    public sealed class Lease(SimulationGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Exit();
            }
        }
    }
}
