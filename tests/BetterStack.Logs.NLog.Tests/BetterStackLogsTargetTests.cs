using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Xunit;

namespace BetterStack.Logs.NLog.Tests
{
    public class BetterStackLogsTargetTests : IDisposable
    {
        private readonly FakeIngestion ingestion = new FakeIngestion();
        private readonly LogFactory logFactory = new LogFactory();
        private readonly System.IO.TextWriter originalInternalLogWriter = InternalLogger.LogWriter;
        private readonly LogLevel originalInternalLogLevel = InternalLogger.LogLevel;

        public void Dispose()
        {
            logFactory.Shutdown();
            ingestion.Dispose();
            GlobalDiagnosticsContext.Clear();
            InternalLogger.LogWriter = originalInternalLogWriter;
            InternalLogger.LogLevel = originalInternalLogLevel;
        }

        private BetterStackLogsTarget Target() => new BetterStackLogsTarget {
            Name = "betterstack",
            SourceToken = "test-source-token",
            Endpoint = ingestion.Endpoint,
            Layout = "${message}",
            FlushPeriodMilliseconds = 10,
        };

        private Logger LoggerFor(BetterStackLogsTarget target)
        {
            var config = new LoggingConfiguration(logFactory);
            config.AddRuleForAllLevels(target);
            logFactory.Configuration = config;

            return logFactory.GetLogger("TestLogger");
        }

        [Fact]
        public void SendsLogsAsJsonArrayWithSourceToken()
        {
            LoggerFor(Target()).Warn("Something is happening!");

            var request = ingestion.NextRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("/", request.Path);
            Assert.Equal("Bearer test-source-token", request.Authorization);
            Assert.Equal("application/json", request.ContentType);

            var log = Assert.Single(request.Logs);
            Assert.Equal("Something is happening!", (string)log["message"]);
            Assert.Equal("Warn", (string)log["level"]);
            Assert.Equal("TestLogger", (string)log["context"]["logger"]);
        }

        [Fact]
        public void SendsTimestampOfTheLogEvent()
        {
            var logEvent = new LogEventInfo(LogLevel.Info, "TestLogger", "Hello") {
                TimeStamp = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc),
            };
            LoggerFor(Target()).Log(logEvent);

            var log = Assert.Single(ingestion.NextRequest().Logs);
            Assert.Equal("2026-01-02T03:04:05.678+00:00", (string)log["dt"]);
        }

        [Fact]
        public void SendsMessageRenderedByLayout()
        {
            var target = Target();
            target.Layout = "${level:uppercase=true}|${logger}|${message}";
            LoggerFor(target).Error("Order {orderId} failed", 75423);

            var log = Assert.Single(ingestion.NextRequest().Logs);
            Assert.Equal("ERROR|TestLogger|Order 75423 failed", (string)log["message"]);
        }

        [Fact]
        public void SendsExceptionOfTheLogEvent()
        {
            LoggerFor(Target()).Error(new InvalidOperationException("Payment gateway timed out"), "Order {orderId} failed", 75423);

            var log = Assert.Single(ingestion.NextRequest().Logs);
            Assert.Equal("Order 75423 failed", (string)log["message"]);
            Assert.Equal("System.InvalidOperationException: Payment gateway timed out", (string)log["exception"]);
        }

        [Fact]
        public void OmitsExceptionWhenThereIsNone()
        {
            LoggerFor(Target()).Error("Order {orderId} failed", 75423);

            var log = (JObject)Assert.Single(ingestion.NextRequest().Logs);
            Assert.False(log.ContainsKey("exception"));
        }

        [Fact]
        public void SendsStructuredProperties()
        {
            LoggerFor(Target()).Info("User {user} - {userID} just ordered item {item}", "Josh", 95845, 75423);

            var properties = Assert.Single(ingestion.NextRequest().Logs)["context"]["properties"];
            Assert.Equal("Josh", (string)properties["user"]);
            Assert.Equal(95845, (int)properties["userID"]);
            Assert.Equal(75423, (int)properties["item"]);
        }

        [Fact]
        public void SendsEmptyPropertiesForPlainMessage()
        {
            LoggerFor(Target()).Info("Hello");

            var context = Assert.Single(ingestion.NextRequest().Logs)["context"];
            Assert.Equal("{}", context["properties"].ToString());
        }

        [Fact]
        public void SendsSourceLocation()
        {
            LoggerFor(Target()).Info("Hello");

            var runtime = Assert.Single(ingestion.NextRequest().Logs)["context"]["runtime"];
            Assert.Equal("BetterStack.Logs.NLog.Tests.BetterStackLogsTargetTests", (string)runtime["class"]);
            Assert.Equal("SendsSourceLocation", (string)runtime["member"]);
            Assert.EndsWith("BetterStackLogsTargetTests.cs", (string)runtime["file"]);
            Assert.Equal(JTokenType.Integer, runtime["line"].Type);
        }

        [Fact]
        public void OmitsSourceLocationWhenCaptureIsDisabled()
        {
            var target = Target();
            target.CaptureSourceLocation = false;
            LoggerFor(target).Info("Hello");

            var runtime = Assert.Single(ingestion.NextRequest().Logs)["context"]["runtime"];
            Assert.Equal(JTokenType.Null, runtime["class"].Type);
            Assert.Equal(JTokenType.Null, runtime["member"].Type);
            Assert.Equal(JTokenType.Null, runtime["file"].Type);
            Assert.Equal(JTokenType.Null, runtime["line"].Type);
        }

        [Fact]
        public void SendsGlobalDiagnosticsContextByDefault()
        {
            GlobalDiagnosticsContext.Set("environment", "staging");
            LoggerFor(Target()).Info("Hello");

            var context = Assert.Single(ingestion.NextRequest().Logs)["context"];
            Assert.Equal("staging", (string)context["gdc"]["environment"]);
        }

        [Fact]
        public void OmitsGlobalDiagnosticsContextWhenDisabled()
        {
            GlobalDiagnosticsContext.Set("environment", "staging");
            var target = Target();
            target.IncludeGlobalDiagnosticContext = false;
            LoggerFor(target).Info("Hello");

            var context = (JObject)Assert.Single(ingestion.NextRequest().Logs)["context"];
            Assert.False(context.ContainsKey("gdc"));
        }

        [Fact]
        public void SendsObjectPropertiesAsJson()
        {
            var order = new { Id = 75423, Items = new[] { "book", "pen" }, PlacedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Note = (string)null };
            LoggerFor(Target()).Info("Order {@order} placed in {elapsed}", order, TimeSpan.FromSeconds(1.5));

            var properties = Assert.Single(ingestion.NextRequest().Logs)["context"]["properties"];
            Assert.Equal(@"{""id"":75423,""items"":[""book"",""pen""],""placedAt"":""2026-01-02T03:04:05Z"",""note"":null}", properties["order"].ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal("00:00:01.5000000", (string)properties["elapsed"]);
        }

        [Fact]
        public void OmitsScopePropertiesByDefault()
        {
            var logger = LoggerFor(Target());

            using (PushScopeProperty("requestId", "req-123")) {
                logger.Info("User {user} signed in", "Josh");
            }

            var properties = (JObject)Assert.Single(ingestion.NextRequest().Logs)["context"]["properties"];
            Assert.Equal(new[] { "user" }, properties.Properties().Select(property => property.Name));
        }

        [Fact]
        public void SendsScopePropertiesWhenEnabled()
        {
            var logger = LoggerFor(TargetWithScopeProperties());

            using (PushScopeProperty("requestId", "req-123")) {
                logger.Info("User {user} signed in", "Josh");
            }
            logger.Info("Outside of the scope");

            var logs = ingestion.NextRequest().Logs;
            Assert.Equal("req-123", (string)logs[0]["context"]["properties"]["requestId"]);
            Assert.Equal("Josh", (string)logs[0]["context"]["properties"]["user"]);
            Assert.Equal("{}", logs[1]["context"]["properties"].ToString());
        }

        [Fact]
        public void SendsScopePropertiesCapturedBeforeAsyncWrapper()
        {
            var config = new LoggingConfiguration(logFactory);
            config.AddRuleForAllLevels(new AsyncTargetWrapper("async", TargetWithScopeProperties()));
            logFactory.Configuration = config;

            // The wrapper writes on its own thread, after the scope is gone from the logging thread
            using (PushScopeProperty("requestId", "req-123")) {
                logFactory.GetLogger("TestLogger").Info("Hello");
            }

            var log = Assert.Single(ingestion.NextRequest().Logs);
            Assert.Equal("req-123", (string)log["context"]["properties"]["requestId"]);
        }

        [Fact]
        public void SendsContextPropertiesConfiguredOnTarget()
        {
            var target = Target();
            target.ContextProperties.Add(new TargetPropertyWithContext("service", "checkout"));
            LoggerFor(target).Info("Order {orderId} placed", 75423);

            var properties = Assert.Single(ingestion.NextRequest().Logs)["context"]["properties"];
            Assert.Equal("checkout", (string)properties["service"]);
            Assert.Equal(75423, (int)properties["orderId"]);
        }

        [Fact]
        public void ConfiguresContextPropertiesFromXml()
        {
            var xml = $@"
                <nlog>
                    <extensions>
                        <add assembly=""BetterStack.Logs.NLog"" />
                    </extensions>
                    <targets>
                        <target type=""BetterStack.Logs"" name=""betterstack"" layout=""${{message}}""
                            sourceToken=""xml-source-token"" endpoint=""{ingestion.Endpoint}"" flushPeriodMilliseconds=""10"">
                            <contextproperty name=""service"" layout=""checkout"" />
                        </target>
                    </targets>
                    <rules>
                        <logger name=""*"" minlevel=""Trace"" writeTo=""betterstack"" />
                    </rules>
                </nlog>";
            logFactory.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml, logFactory);

            logFactory.GetLogger("TestLogger").Info("Hello");

            var properties = Assert.Single(ingestion.NextRequest().Logs)["context"]["properties"];
            Assert.Equal("checkout", (string)properties["service"]);
        }

        [Fact]
        public void OmitsEventPropertiesWhenDisabled()
        {
            var target = Target();
            target.IncludeEventProperties = false;
            LoggerFor(target).Info("Order {orderId} placed", 75423);

            var context = Assert.Single(ingestion.NextRequest().Logs)["context"];
            Assert.Equal("{}", context["properties"].ToString());
        }

        private BetterStackLogsTarget TargetWithScopeProperties()
        {
            var target = Target();
#if NLOG_4
            target.IncludeMdlc = true;
#else
            target.IncludeScopeProperties = true;
#endif
            return target;
        }

        private static IDisposable PushScopeProperty(string name, string value)
        {
#if NLOG_4
            return MappedDiagnosticsLogicalContext.SetScoped(name, value);
#else
            return ScopeContext.PushProperty(name, value);
#endif
        }

        [Fact]
        public void SplitsLogsIntoBatches()
        {
            var target = Target();
            target.MaxBatchSize = 2;
            target.FlushPeriodMilliseconds = 500;
            var logger = LoggerFor(target);

            for (var i = 1; i <= 5; i++) logger.Info("Log " + i);

            var batches = Enumerable.Range(0, 3).Select(_ => ingestion.NextRequest().Logs.Select(log => (string)log["message"]).ToArray()).ToArray();
            Assert.Equal(new[] { "Log 1", "Log 2" }, batches[0]);
            Assert.Equal(new[] { "Log 3", "Log 4" }, batches[1]);
            Assert.Equal(new[] { "Log 5" }, batches[2]);
        }

        [Fact]
        public void DeliversPendingLogsOnShutdown()
        {
            var target = Target();
            target.FlushPeriodMilliseconds = 60000;
            LoggerFor(target).Info("Last words");

            logFactory.Shutdown();

            Assert.True(ingestion.HasRequest, "Shutdown returned before the pending log was delivered.");
            Assert.Equal("Last words", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
        }

        [Fact]
        public void RetriesTheBatchAfterServerError()
        {
            ingestion.StatusCodes.Enqueue(500);
            LoggerFor(Target()).Info("Hello");

            var failed = ingestion.NextRequest();
            var retried = ingestion.NextRequest();
            Assert.Equal("Hello", (string)Assert.Single(failed.Logs)["message"]);
            Assert.Equal(failed.Body, retried.Body);
        }

        [Fact]
        public void DropsNewLogsOnceTheQueueIsFull()
        {
            var internalLog = new System.IO.StringWriter();
            InternalLogger.LogLevel = LogLevel.Error;
            InternalLogger.LogWriter = internalLog;
            ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.MaxQueueSize = 5;
            var logger = LoggerFor(target);

            logger.Info("Stuck");
            ingestion.NextRequest();
            // Written while the failed batch waits a second for its retry
            for (var i = 1; i <= 8; i++) logger.Info("Log " + i);

            Assert.Equal("Stuck", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
            Assert.Equal(new[] { "Log 1", "Log 2", "Log 3", "Log 4", "Log 5" }, ingestion.NextRequest().Logs.Select(log => (string)log["message"]));
            var error = Assert.Single(internalLog.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries));
            Assert.EndsWith("Error BetterStack.Logs: maximum number of logs in the queue reached (5). New logs will be dropped.", error);
        }

        [Fact]
        public void ReportsTheNextOverflowOnceTheQueueHasRoomAgain()
        {
            var internalLog = new System.IO.StringWriter();
            InternalLogger.LogLevel = LogLevel.Error;
            InternalLogger.LogWriter = internalLog;
            var target = Target();
            target.MaxQueueSize = 1;
            var logger = LoggerFor(target);

            for (var round = 1; round <= 2; round++) {
                ingestion.StatusCodes.Enqueue(500);
                logger.Info("Stuck");
                ingestion.NextRequest();
                // Written while the failed batch waits a second for its retry
                logger.Info("Queued");
                logger.Info("Dropped");
                ingestion.NextRequest();
                Assert.Equal("Queued", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
            }

            var errors = internalLog.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, errors.Length);
            Assert.All(errors, error => Assert.EndsWith("Error BetterStack.Logs: maximum number of logs in the queue reached (1). New logs will be dropped.", error));
        }

        [Fact]
        public void KeepsAtMost100000LogsInTheQueueByDefault()
        {
            Assert.Equal(100000, new BetterStackLogsTarget().MaxQueueSize);
        }

        [Fact]
        public void ReportsMaxQueueSizeBelowOneAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.MaxQueueSize = 0;

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: maxQueueSize is 0. Set it to 1 or more.", exception.Message);
        }

        [Fact]
        public void KeepsDeliveringAfterBatchRanOutOfRetries()
        {
            ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.Retries = 1;
            var logger = LoggerFor(target);

            logger.Info("Dropped");
            Assert.Equal("Dropped", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            logger.Info("Delivered");
            Assert.Equal("Delivered", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
        }

        [Fact]
        public void ConfiguresFromXml()
        {
            var xml = $@"
                <nlog>
                    <extensions>
                        <add assembly=""BetterStack.Logs.NLog"" />
                    </extensions>
                    <targets>
                        <target type=""BetterStack.Logs"" name=""betterstack"" layout=""${{message}}""
                            sourceToken=""xml-source-token"" endpoint=""{ingestion.Endpoint}""
                            maxBatchSize=""200"" flushPeriodMilliseconds=""10"" retries=""3""
                            captureSourceLocation=""false"" includeGlobalDiagnosticContext=""false"" />
                    </targets>
                    <rules>
                        <logger name=""*"" minlevel=""Trace"" writeTo=""betterstack"" />
                    </rules>
                </nlog>";
            logFactory.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml, logFactory);

            var target = logFactory.Configuration.FindTargetByName<BetterStackLogsTarget>("betterstack");
            Assert.Equal(200, target.MaxBatchSize);
            Assert.Equal(10, target.FlushPeriodMilliseconds);
            Assert.Equal(3, target.Retries);
            Assert.False(target.CaptureSourceLocation);
            Assert.False(target.IncludeGlobalDiagnosticContext);

            logFactory.GetLogger("TestLogger").Trace("Tracing the code!");

            var request = ingestion.NextRequest();
            Assert.Equal("Bearer xml-source-token", request.Authorization);
            var log = Assert.Single(request.Logs);
            Assert.Equal("Tracing the code!", (string)log["message"]);
            Assert.Equal("Trace", (string)log["level"]);
        }
    }
}
