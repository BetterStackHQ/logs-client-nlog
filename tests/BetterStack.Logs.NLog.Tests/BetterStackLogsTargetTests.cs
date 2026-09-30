using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using NLog;
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

        public void Dispose()
        {
            logFactory.Shutdown();
            ingestion.Dispose();
            GlobalDiagnosticsContext.Clear();
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
        public void DeliversPendingLogsOnFlush()
        {
            var target = Target();
            target.FlushPeriodMilliseconds = 60000;
            var logger = LoggerFor(target);

            logger.Info("Before the first flush");
            logFactory.Flush(TimeSpan.FromSeconds(10));

            Assert.True(ingestion.HasRequest, "Flush returned before the pending log was delivered.");
            Assert.Equal("Before the first flush", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            logger.Info("Before the second flush");
            logFactory.Flush(TimeSpan.FromSeconds(10));

            Assert.True(ingestion.HasRequest, "The second flush returned before the pending log was delivered.");
            Assert.Equal("Before the second flush", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
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
        public void StopsWaitingForDeliveryOnShutdownAfterMaxFlushTime()
        {
            for (var i = 0; i < 20; i++) ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.MaxFlushTimeMilliseconds = 500;
            LoggerFor(target).Info("Never delivered");

            var stopwatch = Stopwatch.StartNew();
            logFactory.Shutdown();

            // Without the limit, the ten attempts with their back-off hold the shutdown for 45 seconds. With it, the
            // flush that Shutdown() starts with and the close that follows share the 500 ms.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}.");
        }

        [Fact]
        public void StopsWaitingForDeliveryOnFlushAfterMaxFlushTime()
        {
            for (var i = 0; i < 20; i++) ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.FlushPeriodMilliseconds = 60000;
            target.MaxFlushTimeMilliseconds = 500;
            LoggerFor(target).Info("Never delivered");

            var stopwatch = Stopwatch.StartNew();
            logFactory.Flush(TimeSpan.FromSeconds(20));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Flush took {stopwatch.Elapsed}.");
        }

        [Fact]
        public void WaitsTheFullMaxFlushTimeOnCloseAfterDeliveredFlush()
        {
            var target = Target();
            target.FlushPeriodMilliseconds = 60000;
            target.MaxFlushTimeMilliseconds = 1500;
            var logger = LoggerFor(target);

            logger.Info("Delivered on flush");
            logFactory.Flush(TimeSpan.FromSeconds(20));
            Assert.Equal("Delivered on flush", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            // Longer ago than the limit, so the delivered flush would use it all up if it still counted
            System.Threading.Thread.Sleep(1500);
            for (var i = 0; i < 20; i++) ingestion.StatusCodes.Enqueue(500);
            logger.Info("Never delivered");

            // Closes the target without the flush that Shutdown() starts with, which would wait the full limit anyway
            var stopwatch = Stopwatch.StartNew();
            target.Dispose();

            Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(1), $"Closing took {stopwatch.Elapsed}.");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"Closing took {stopwatch.Elapsed}.");
        }

        [Fact]
        public void WaitsForDeliveryOnShutdownWithoutLimitWhenMaxFlushTimeIsZero()
        {
            ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.FlushPeriodMilliseconds = 60000;
            target.MaxFlushTimeMilliseconds = 0;
            target.Retries = 3;
            LoggerFor(target).Info("Last words");

            logFactory.Shutdown();

            Assert.Equal("Last words", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
            Assert.True(ingestion.HasRequest, "Shutdown returned before the retried log was delivered.");
            Assert.Equal("Last words", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
        }

        [Fact]
        public void ConfiguresMaxFlushTimeFromXml()
        {
            Assert.Equal(30000, new BetterStackLogsTarget().MaxFlushTimeMilliseconds);

            var xml = $@"
                <nlog>
                    <extensions>
                        <add assembly=""BetterStack.Logs.NLog"" />
                    </extensions>
                    <targets>
                        <target type=""BetterStack.Logs"" name=""betterstack"" layout=""${{message}}""
                            sourceToken=""xml-source-token"" endpoint=""{ingestion.Endpoint}"" maxFlushTimeMilliseconds=""5000"" />
                    </targets>
                    <rules>
                        <logger name=""*"" minlevel=""Trace"" writeTo=""betterstack"" />
                    </rules>
                </nlog>";
            logFactory.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml, logFactory);

            Assert.Equal(5000, logFactory.Configuration.FindTargetByName<BetterStackLogsTarget>("betterstack").MaxFlushTimeMilliseconds);
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

        [Fact]
        public void DisposedClientSendsNothing()
        {
            var client = new Client("test-source-token", ingestion.Endpoint, retries: 1);
            client.Dispose();

            client.Send(new[] { new Log { Message = "After dispose" } }).Wait();

            Assert.False(ingestion.HasRequest, "The disposed client still sent the log.");
        }

        [Fact]
        public void DisposesTheClientWhenTheTargetIsReloadedOrClosed()
        {
            using (var server = new KeepAliveServer()) {
                var first = Target();
                first.Endpoint = server.Endpoint;
                LoggerFor(first).Info("Before the reload");
                var firstConnection = server.AnswerRequest();

                var second = Target();
                second.Endpoint = server.Endpoint;
                LoggerFor(second).Info("After the reload");
                var secondConnection = server.AnswerRequest();

                logFactory.Shutdown();

                // A client keeps its connection open until it is disposed
                Assert.Equal(-1, firstConnection.ReadByte());
                Assert.Equal(-1, secondConnection.ReadByte());
            }
        }
    }
}
