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
        private readonly int maxQueueSize;
        private readonly Client client;
        private readonly TimeSpan period;

        private object taskLock = new object();
        private readonly Task runningTask;

        private ConcurrentQueue<Log> queue = new ConcurrentQueue<Log>();
        private CancellationTokenSource cancellationTokenSource;
        // Kept on every enqueue and dequeue: ConcurrentQueue.Count walks all segments of the queue
        private int queueLength;
        // 1 from the first log dropped on a full queue until the drain takes logs off the queue again
        private int overflowReported;

        /// <summary>
        /// Initializes a Better Stack Logs drain and starts periodic logs delivery.
        /// </summary>
        public Drain(
            Client client,
            TimeSpan? period = null,
            int maxBatchSize = 1000,
            CancellationToken? cancellationToken = null
        ) : this(client, period, maxBatchSize, 100000, cancellationToken)
        {
        }

        /// <summary>
        /// Initializes a Better Stack Logs drain that holds at most maxQueueSize logs waiting to be delivered,
        /// and starts periodic logs delivery.
        /// </summary>
        public Drain(
            Client client,
            TimeSpan? period,
            int maxBatchSize,
            int maxQueueSize,
            CancellationToken? cancellationToken = null
        )
        {
            this.client = client;
            this.period = period ?? TimeSpan.FromMilliseconds(250);
            this.maxBatchSize = maxBatchSize;
            this.maxQueueSize = maxQueueSize;
            this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);

            runningTask = Task.Run(run);
        }

        /// <summary>
        /// Adds a single log event to a queue. The log event will be delivered later in a batch.
        /// The log event is dropped when the queue already holds maxQueueSize of them.
        /// This method will throw an exception if the Drain is stopped.
        /// </summary>
        public void Enqueue(Log log)
        {
            if (cancellationTokenSource.IsCancellationRequested) throw new DrainIsClosedException();

            // Like in our Java client, a full queue drops the new log and keeps the ones it holds
            if (Interlocked.Increment(ref queueLength) > maxQueueSize) {
                Interlocked.Decrement(ref queueLength);
                if (Interlocked.Exchange(ref overflowReported, 1) == 0) {
                    global::NLog.Common.InternalLogger.Error("BetterStack.Logs: maximum number of logs in the queue reached ({0}). New logs will be dropped.", maxQueueSize);
                }
                return;
            }

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
        }

        private async Task flush() {
            while (!queue.IsEmpty) {
                var expectedItemsCount = Math.Min(maxBatchSize, queue.Count);
                var nextBatch = new List<Log>(expectedItemsCount);

                while (!queue.IsEmpty && nextBatch.Count < maxBatchSize) {
                    if (queue.TryDequeue(out var log)) {
                        Interlocked.Decrement(ref queueLength);
                        nextBatch.Add(log);
                    }
                }

                if (nextBatch.Count > 0) {
                    // The queue has room again: its next overflow is reported again
                    Volatile.Write(ref overflowReported, 0);
                    await client.Send(nextBatch);
                }
            }
        }

        private async Task<TimeSpan> delayed(Func<Task> asyncAction, TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            try {
                await Task.Delay(delay, cancellationTokenSource.Token);
            } catch (TaskCanceledException) {
                // finish the rest of the loop to flush everything
            }

            var start = DateTimeOffset.UtcNow;

            await asyncAction();

            return DateTimeOffset.UtcNow - start;
        }
    }
}
