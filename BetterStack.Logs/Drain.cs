using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterStack.Logs
{
    /// <summary>
    /// The Drain class is responsible for maintaining a queue of log events that need
    /// to be delivered to the server and periodically forwarding them to the server
    /// in batches.
    /// </summary>
    public sealed class Drain
    {
        private readonly int maxBatchSize;
        private readonly Client client;
        private readonly TimeSpan period;

        private object taskLock = new object();
        private readonly Task runningTask;

        private ConcurrentQueue<Log> queue = new ConcurrentQueue<Log>();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> flushRequests = new ConcurrentQueue<TaskCompletionSource<bool>>();
        private readonly SemaphoreSlim flushSignal = new SemaphoreSlim(0);
        private CancellationTokenSource cancellationTokenSource;

        /// <summary>
        /// Initializes a Better Stack Logs drain and starts periodic logs delivery.
        /// </summary>
        public Drain(
            Client client,
            TimeSpan? period = null,
            int maxBatchSize = 1000,
            CancellationToken? cancellationToken = null
        )
        {
            this.client = client;
            this.period = period ?? TimeSpan.FromMilliseconds(250);
            this.maxBatchSize = maxBatchSize;
            this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);

            runningTask = Task.Run(run);
        }

        /// <summary>
        /// Adds a single log event to a queue. The log event will be delivered later in a batch.
        /// This method will throw an exception if the Drain is stopped.
        /// </summary>
        public void Enqueue(Log log)
        {
            if (cancellationTokenSource.IsCancellationRequested) throw new DrainIsClosedException();

            queue.Enqueue(log);
        }

        /// <summary>
        /// Stops periodic logs delivery. The returned task will complete once the queue is flushed.
        /// </summary>
        public async Task Stop()
        {
            cancellationTokenSource.Cancel();
            await runningTask;
        }

        /// <summary>
        /// Delivers the queued logs now instead of waiting for the next period. The returned task
        /// completes once every log enqueued before the call has been sent or given up on.
        /// </summary>
        public Task Flush()
        {
            var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            flushRequests.Enqueue(request);
            flushSignal.Release();

            // A stopped drain has delivered everything already and picks up no more requests
            if (runningTask.IsCompleted) request.TrySetResult(true);

            return request.Task;
        }

        private async Task run() {
            var nextDelay = period;

            // XXX: We want the loop to run at least once, even if we stop
            //      the drain before the we manage to reach this point.
            do {
                try {
                    var flushDuration = await delayed(flush, nextDelay);
                    nextDelay = period - flushDuration;
                } catch (Exception ex) {
                    // This task must survive anything a single flush can throw. If it faults, the
                    // loop is gone for good: nothing is ever delivered again and every subsequent
                    // Enqueue is silently swallowed until the process restarts.
                    global::NLog.Common.InternalLogger.Error(ex, "BetterStack.Logs: log delivery failed, retrying after the next period.");
                    nextDelay = period;
                }
            } while (!cancellationTokenSource.IsCancellationRequested);

            while (flushRequests.TryDequeue(out var request)) request.TrySetResult(true);
        }

        private async Task flush() {
            // Taken before the queue is drained, so everything enqueued ahead of these requests is sent first
            var requests = new List<TaskCompletionSource<bool>>();
            while (flushRequests.TryDequeue(out var request)) requests.Add(request);

            try {
                while (!queue.IsEmpty) {
                    var expectedItemsCount = Math.Min(maxBatchSize, queue.Count);
                    var nextBatch = new List<Log>(expectedItemsCount);

                    while (!queue.IsEmpty && nextBatch.Count < maxBatchSize) {
                        if (queue.TryDequeue(out var log)) nextBatch.Add(log);
                    }

                    if (nextBatch.Count > 0) {
                        await client.Send(nextBatch);
                    }
                }
            } finally {
                foreach (var request in requests) request.TrySetResult(true);
            }
        }

        private async Task<TimeSpan> delayed(Func<Task> asyncAction, TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            try {
                // Waits for the period to pass, or for a Flush() to ask for delivery right away
                await flushSignal.WaitAsync(delay, cancellationTokenSource.Token);
            } catch (OperationCanceledException) {
                // finish the rest of the loop to flush everything
            }

            var start = DateTimeOffset.UtcNow;

            await asyncAction();

            return DateTimeOffset.UtcNow - start;
        }
    }
}
