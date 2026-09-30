using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using NLog.Layouts;

namespace BetterStack.Logs.NLog
{
    /// <summary>
    /// NLog target for Better Stack Logs. This target does not send all the events individually
    /// to the Better Stack server but it sends them periodically in batches.
    /// </summary>
    [Target("BetterStack.Logs")]
    public sealed class BetterStackLogsTarget : TargetWithContext
    {
        /// <summary>
        /// Gets or sets the Better Stack Logs source token.
        /// </summary>
        /// <value>The source token.</value>
        [RequiredParameter]
        public Layout SourceToken { get; set; }

        /// <summary>
        /// The Better Stack Logs endpoint.
        /// </summary>
        public Layout Endpoint { get; set; } = "https://in.logs.betterstack.com";

        /// <summary>
        /// Maximum logs sent to the server in one batch.
        /// </summary>
        public int MaxBatchSize { get; set; } = 1000;

        /// <summary>
        /// The flushing period in milliseconds.
        /// </summary>
        public int FlushPeriodMilliseconds { get; set; } = 250;

        /// <summary>
        /// The number of retries of failing HTTP requests.
        /// </summary>
        public int Retries { get; set; } = 10;

        /// <summary>
        /// The maximum time in milliseconds that flushing or closing the target, on shutdown or when the configuration
        /// is reloaded, waits for queued logs to be sent. A flush followed by a close, as on shutdown, shares this time.
        /// Logs not sent by then are lost when the application exits, so an endpoint that cannot be reached does not
        /// hold the shutdown. 0 means no limit.
        /// </summary>
        public int MaxFlushTimeMilliseconds { get; set; } = 30000;

        /// <summary>
        /// We capture the file and line of every log message by default. You can turn this
        /// option off if it has negative impact on the performance of your application.
        /// </summary>
        public bool CaptureSourceLocation
        {
            get => StackTraceUsage == StackTraceUsage.Max;
            set => StackTraceUsage = value ? StackTraceUsage.Max : StackTraceUsage.None;
        }

        /// <summary>
        /// Include GlobalDiagnosticContext in logs.
        /// </summary>
        public bool IncludeGlobalDiagnosticContext { get; set; } = true;

        /// <summary>
        /// Control callsite capture of source-file and source-linenumber.
        /// </summary>
        public StackTraceUsage StackTraceUsage
        {
            get => _stackTraceUsage;
            set
            {
                if (value == StackTraceUsage.None)
                {
                    IncludeCallSite = false;
                    IncludeCallSiteStackTrace = false;
                }
                else
                {
                    IncludeCallSite = true;
                    IncludeCallSiteStackTrace = value == StackTraceUsage.Max;
                }
                _stackTraceUsage = value;
            }
        }
        private StackTraceUsage _stackTraceUsage;

        private Drain betterStackDrain = null;
        private Client betterStackClient = null;
        // The last flush asked of the drain, and the time since which a flush has been waiting for it
        private Task pendingFlush;
        private Stopwatch pendingFlushTime;

        /// <summary>
        /// Initializes a new instance of the BetterStack.Logs.NLog.BetterStackLogsTarget class.
        /// </summary>
        public BetterStackLogsTarget()
        {
            StackTraceUsage = StackTraceUsage.Max;
            IncludeEventProperties = true;
        }

        /// <inheritdoc/>
        protected override void InitializeTarget()
        {
            stopDrain();

            var sourceToken = RenderLogEvent(SourceToken, LogEventInfo.CreateNullEvent());
            var endpoint = RenderLogEvent(Endpoint, LogEventInfo.CreateNullEvent());

            var client = new Client(
                sourceToken,
                endpoint: endpoint,
                retries: Retries
            );

            betterStackDrain = new Drain(
                client,
                period: TimeSpan.FromMilliseconds(FlushPeriodMilliseconds),
                maxBatchSize: MaxBatchSize
            );
            betterStackClient = client;

            base.InitializeTarget();
        }

        /// <inheritdoc/>
        protected override void CloseTarget()
        {
            stopDrain();
            base.CloseTarget();
        }

        /// <inheritdoc/>
        protected override void FlushAsync(AsyncContinuation asyncContinuation)
        {
            var flush = betterStackDrain.Flush();
            lock (SyncRoot) {
                if (pendingFlush == null || pendingFlush.IsCompleted) pendingFlushTime = Stopwatch.StartNew();
                pendingFlush = flush;
            }

            var timeout = MaxFlushTimeMilliseconds > 0 ? MaxFlushTimeMilliseconds : System.Threading.Timeout.Infinite;
            Task.WhenAny(flush, Task.Delay(timeout)).ContinueWith(first => {
                if (first.Result != flush) {
                    global::NLog.Common.InternalLogger.Warn("BetterStack.Logs: gave up waiting for queued logs to be sent on flush after {0} ms (maxFlushTimeMilliseconds).", MaxFlushTimeMilliseconds);
                }
                asyncContinuation(flush.Exception);
            });
        }

        /// <inheritdoc/>
        protected override void Write(LogEventInfo logEvent)
        {
            var contextDictionary = new Dictionary<string, object> {
                ["logger"] = logEvent.LoggerName,
                // Event properties, plus whatever else the target is configured to include:
                // scope properties (IncludeScopeProperties, or IncludeMdlc on NLog 4) and <contextproperty> items
                ["properties"] = GetAllProperties(logEvent),
                ["runtime"] = new Dictionary<string, object> {
                    ["class"] = logEvent.CallerClassName,
                    ["member"] = logEvent.CallerMemberName,
                    ["file"] = string.IsNullOrEmpty(logEvent.CallerFilePath) ? null : logEvent.CallerFilePath,
                    ["line"] = string.IsNullOrEmpty(logEvent.CallerFilePath) ? null : logEvent.CallerLineNumber as int?,
                },
            };

            if (IncludeGlobalDiagnosticContext) {
                var gdcKeys = GlobalDiagnosticsContext.GetNames();

                if (gdcKeys.Count > 0) {
                    var gdcDict = new Dictionary<string, object>();

                    foreach (string key in gdcKeys) {
                        if (string.IsNullOrEmpty(key)) continue;
                        gdcDict[key] = GlobalDiagnosticsContext.GetObject(key);
                    }

                    contextDictionary["gdc"] = gdcDict;
                }
            }
            string logMessage = RenderLogEvent(this.Layout, logEvent);

            var log = new Log {
                Timestamp = new DateTimeOffset(logEvent.TimeStamp),
                Message = logMessage,
                Level = logEvent.Level.Name,
                Exception = logEvent.Exception?.ToString(),
                Context = contextDictionary
            };

            betterStackDrain.Enqueue(log);
        }

        private void stopDrain()
        {
            if (betterStackDrain == null) return;

            var timeout = remainingFlushTime();
            var stopped = betterStackDrain.Stop();

            // Disposed once the drain has stopped, also when that is after the wait below: a drain still retrying in
            // the background needs its client until then. The reference is cleared because a closed target that is
            // initialized again stops the same drain once more.
            var client = betterStackClient;
            betterStackClient = null;
            if (client != null) stopped.ContinueWith(_ => client.Dispose());

            if (!stopped.Wait(timeout)) {
                // The drain delivers on a thread-pool thread, which does not keep the process alive: what it has not
                // sent yet is lost when the application exits, and still sent in the background after a reload
                global::NLog.Common.InternalLogger.Warn("BetterStack.Logs: gave up waiting for queued logs to be sent after {0} ms (maxFlushTimeMilliseconds).", MaxFlushTimeMilliseconds);
            }
            lock (SyncRoot) pendingFlush = null;
        }

        // How long closing may still wait for delivery. Waiting on a flush the drain has not delivered yet counts
        // against it, so that LogFactory.Shutdown(), which flushes and then closes, waits MaxFlushTimeMilliseconds
        // in total.
        private int remainingFlushTime()
        {
            if (MaxFlushTimeMilliseconds <= 0) return System.Threading.Timeout.Infinite;

            lock (SyncRoot) {
                if (pendingFlush == null || pendingFlush.IsCompleted) return MaxFlushTimeMilliseconds;
                return (int)Math.Max(0, MaxFlushTimeMilliseconds - pendingFlushTime.ElapsedMilliseconds);
            }
        }
    }
}
