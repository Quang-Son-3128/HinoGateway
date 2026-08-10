using System.Collections.Concurrent;

namespace HinoGateway.Engine;

/// <summary>
/// Runs one asynchronous loop on its own dedicated OS thread — <em>including every continuation
/// after an <c>await</c></em>.
/// <para>
/// This exists because industrial protocol clients are rarely async underneath. NModbus 3.0.83, for
/// example, exposes <c>*Async</c> methods that have no async state machine at all: the socket I/O is
/// synchronous and holds the calling thread for the whole request. Running such a device loop with
/// <see cref="Task.Run(Func{Task})"/> puts it on the thread pool, where a few dozen slow links empty
/// the pool — the pool only grows by roughly one thread per second, so timers stop firing, other
/// devices stop being polled and their tags go Bad. Measured on 2026-08-01: 150 devices behind a
/// 200 ms link achieved 12.8 of 65 expected polls with 92/150 connected, and 59 of 65 with 150/150
/// connected once the loops were kept off the pool.
/// </para>
/// <para>
/// A bare <c>LongRunning</c> task is <b>not</b> enough: only the code up to the first suspending
/// <c>await</c> runs on the dedicated thread, and every continuation afterwards is posted to the
/// pool anyway. So the thread installs a single-threaded <see cref="SynchronizationContext"/> and
/// pumps it, which brings continuations back home and keeps the promise the driver contract makes —
/// that a driver is only ever touched by one thread at a time.
/// </para>
/// </summary>
public static class WorkerThread
{
    /// <summary>
    /// Starts <paramref name="body"/> on a new background thread and returns a task that completes
    /// when the body does (faulting or cancelling with it, just like <see cref="Task.Run(Func{Task})"/>).
    /// </summary>
    /// <param name="name">Thread name, so a hung device is identifiable in a dump or debugger.</param>
    /// <param name="body">The loop to run. Its continuations stay on this thread.</param>
    /// <param name="onCallbackError">
    /// Invoked if a queued continuation throws outside of the body's own task — which should not
    /// happen, but must never be allowed to kill the pump and hang shutdown.
    /// </param>
    public static Task Run(string name, Func<Task> body, Action<Exception>? onCallbackError = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        // RunContinuationsAsynchronously: whoever awaits the returned task (shutdown, reload) must not
        // resume on the worker thread that is in the middle of dying.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var pump = new PumpingSynchronizationContext();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                var task = body();

                // Stop pumping once the body is finished. This may fire on any thread, including this
                // one when the body completes synchronously.
                task.ContinueWith(
                    _ => pump.Complete(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                pump.Run(onCallbackError);

                // The pump only stops after the body's task completed, so this never blocks.
                if (task.IsCanceled)
                    completion.TrySetCanceled();
                else if (task.IsFaulted)
                    completion.TrySetException(task.Exception!.InnerExceptions);
                else
                    completion.TrySetResult();
            }
            catch (Exception ex)
            {
                // A body that throws before returning a task.
                completion.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        })
        {
            IsBackground = true, // a stuck device must never keep the process alive
            Name = name,
        };

        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// A message loop: everything posted to it runs, in order, on the thread that calls
    /// <see cref="Run"/>.
    /// </summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The pump has already stopped (the loop finished). Don't drop the work — run it on
                // the pool, where it would have run had this context never existed.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Current == this)
                d(state); // already home
            else
                throw new NotSupportedException("Blocking Send onto a worker thread is not supported.");
        }

        public override SynchronizationContext CreateCopy() => this;

        public void Run(Action<Exception>? onCallbackError)
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    // An exception thrown by an async method's body is captured by its Task and never
                    // arrives here. Anything that does must not stop the pump, or shutdown would hang.
                    onCallbackError?.Invoke(ex);
                }
            }
        }

        public void Complete() => _queue.CompleteAdding();
    }
}
