using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
        public void DoesNotAskForContinueBeforeSendingLogs()
        {
            LoggerFor(Target()).Info("Hello");

            // With "Expect: 100-continue", the client holds the body back until the server answers or 350 ms pass
            Assert.Null(ingestion.NextRequest().Expect);
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
        public void ReportsMissingSourceTokenAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.SourceToken = null;

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: sourceToken is not set. Set it to the source token of your Better Stack source.", exception.Message);
        }

        [Fact]
        public void ReportsEmptyEndpointAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.Endpoint = "";

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: endpoint is empty. Set it to the ingesting host of your Better Stack source.", exception.Message);
        }

        [Fact]
        public void ReportsEndpointWithoutHostAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.Endpoint = "https://";

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: endpoint is \"https://\". Set it to the ingesting host of your Better Stack source.", exception.Message);
        }

        [Fact]
        public void SendsToBareIngestingHostOverHttps()
        {
            // Stands in for the ingesting host: the start of a TLS handshake is all this test needs to see
            var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            server.Start();
            try {
                var target = Target();
                // Uri parses "localhost" of "localhost:1234" as the scheme
                target.Endpoint = $"localhost:{((System.Net.IPEndPoint)server.LocalEndpoint).Port}";
                target.Retries = 1;
                LoggerFor(target).Info("Hello");

                var connection = server.AcceptTcpClientAsync();
                Assert.True(connection.Wait(TimeSpan.FromSeconds(30)), "No connection arrived within 30 seconds.");
                using (var client = connection.Result) {
                    client.ReceiveTimeout = 30000;
                    // A TLS handshake record starts with 0x16, a plain http request with "POST"
                    Assert.Equal(0x16, client.GetStream().ReadByte());
                }
            } finally {
                server.Stop();
            }
        }

        [Fact]
        public void KeepsSchemeOfEndpointInAnyCase()
        {
            var target = Target();
            target.Endpoint = ingestion.Endpoint.Replace("http://", "HTTP://");
            LoggerFor(target).Info("Hello");

            Assert.Equal("Hello", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
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
        public void SendsLogWhoseExceptionCannotBeRendered()
        {
            LoggerFor(Target()).Error(new NastyException(), "Order {orderId} failed", 75423);

            var log = Assert.Single(ingestion.NextRequest().Logs);
            Assert.Equal("Order 75423 failed", (string)log["message"]);
            Assert.Equal("BetterStack.Logs.NLog.Tests.BetterStackLogsTargetTests+NastyException (its ToString() threw System.InvalidOperationException)", (string)log["exception"]);
        }

        private sealed class NastyException : Exception
        {
            public override string Message => throw new InvalidOperationException("The message is gone");
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

        [Theory]
        [InlineData("Max")]
        [InlineData("WithSource")]
#if !NLOG_4
        [InlineData("WithFileNameAndLineNumber")]
#endif
        public void SendsSourceLocationWhenStackTraceUsageAsksForSource(string stackTraceUsage)
        {
            var target = TargetFromXmlWithStackTraceUsage(stackTraceUsage);
            logFactory.GetLogger("TestLogger").Info("Hello");

            var runtime = Assert.Single(ingestion.NextRequest().Logs)["context"]["runtime"];
            Assert.Equal("BetterStack.Logs.NLog.Tests.BetterStackLogsTargetTests", (string)runtime["class"]);
            Assert.Equal("SendsSourceLocationWhenStackTraceUsageAsksForSource", (string)runtime["member"]);
            Assert.EndsWith("BetterStackLogsTargetTests.cs", (string)runtime["file"]);
            Assert.Equal(JTokenType.Integer, runtime["line"].Type);
            Assert.True(target.CaptureSourceLocation);
        }

        [Theory]
        [InlineData("WithoutSource")]
#if !NLOG_4
        [InlineData("WithCallSite")]
        [InlineData("WithCallSiteClassName")]
#endif
        public void SendsClassAndMemberOnlyWhenStackTraceUsageAsksForNoSource(string stackTraceUsage)
        {
            var target = TargetFromXmlWithStackTraceUsage(stackTraceUsage);
            logFactory.GetLogger("TestLogger").Info("Hello");

            var runtime = Assert.Single(ingestion.NextRequest().Logs)["context"]["runtime"];
            Assert.Equal("BetterStack.Logs.NLog.Tests.BetterStackLogsTargetTests", (string)runtime["class"]);
            Assert.Equal("SendsClassAndMemberOnlyWhenStackTraceUsageAsksForNoSource", (string)runtime["member"]);
            Assert.Equal(JTokenType.Null, runtime["file"].Type);
            Assert.Equal(JTokenType.Null, runtime["line"].Type);
            Assert.False(target.CaptureSourceLocation);
        }

        [Fact]
        public void OmitsSourceLocationWhenStackTraceUsageIsNone()
        {
            var target = TargetFromXmlWithStackTraceUsage("None");
            logFactory.GetLogger("TestLogger").Info("Hello");

            var runtime = Assert.Single(ingestion.NextRequest().Logs)["context"]["runtime"];
            Assert.Equal(JTokenType.Null, runtime["class"].Type);
            Assert.Equal(JTokenType.Null, runtime["member"].Type);
            Assert.Equal(JTokenType.Null, runtime["file"].Type);
            Assert.Equal(JTokenType.Null, runtime["line"].Type);
            Assert.False(target.CaptureSourceLocation);
        }

        // The values of StackTraceUsage differ between NLog 4.7, which the library is compiled against, and NLog 5 and
        // later: set in XML, the value is parsed by the NLog loaded at run time
        private BetterStackLogsTarget TargetFromXmlWithStackTraceUsage(string stackTraceUsage)
        {
            var xml = $@"
                <nlog>
                    <extensions>
                        <add assembly=""BetterStack.Logs.NLog"" />
                    </extensions>
                    <targets>
                        <target type=""BetterStack.Logs"" name=""betterstack"" layout=""${{message}}""
                            sourceToken=""xml-source-token"" endpoint=""{ingestion.Endpoint}"" flushPeriodMilliseconds=""10""
                            stackTraceUsage=""{stackTraceUsage}"" />
                    </targets>
                    <rules>
                        <logger name=""*"" minlevel=""Trace"" writeTo=""betterstack"" />
                    </rules>
                </nlog>";
            logFactory.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml, logFactory);

            return logFactory.Configuration.FindTargetByName<BetterStackLogsTarget>("betterstack");
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
        public void CutsOffPropertyNestedTooDeeply()
        {
            Node list = null;
            for (var value = 2000; value >= 1; value--) list = new Node { Value = value, Next = list };
            var target = Target();
            target.FlushPeriodMilliseconds = 500;
            var logger = LoggerFor(target);

            logger.Info("Before");
            logger.Info("{count} nodes in {list}", 2000, list);
            logger.Info("After");

            var logs = ingestion.NextRequest().Logs;
            Assert.Equal("Before", (string)logs[0]["message"]);
            Assert.Equal("After", (string)logs[2]["message"]);
            var properties = logs[1]["context"]["properties"];
            Assert.Equal(2000, (int)properties["count"]);
            Assert.Equal(1, (int)properties["list"]["value"]);
            Assert.Equal(2, (int)properties["list"]["next"]["value"]);
            // Where exactly the list is cut off depends on how deep the property sits in the request
            var nodes = 0;
            for (var node = properties["list"]; node.Type == JTokenType.Object; node = node["next"]) nodes++;
            Assert.InRange(nodes, 2, 63);
        }

        private sealed class Node
        {
            public int Value { get; set; }
            public Node Next { get; set; }
        }

        [Fact]
        public void SendsTaskPropertyAsStringWithoutWaitingForIt()
        {
            var pending = new System.Threading.Tasks.TaskCompletionSource<int>();
            try {
                var logger = LoggerFor(Target());

                logger.Info("Waiting for {task}", pending.Task);
                var properties = Assert.Single(ingestion.NextRequest().Logs)["context"]["properties"];
                Assert.Equal("System.Threading.Tasks.Task`1[System.Int32]", (string)properties["task"]);

                logger.Info("Next");
                Assert.Equal("Next", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
            } finally {
                // A delivery stuck on Task.Result would hold up the shutdown of the target for ever
                pending.SetResult(0);
            }
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
            var target = TargetWithScopeProperties();
            target.FlushPeriodMilliseconds = 500; // both logs have to end up in the same request
            var logger = LoggerFor(target);

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
        public void SplitsBatchIntoRequestsOfAtMostFiveMegabytes()
        {
            var target = Target();
            target.FlushPeriodMilliseconds = 500;
            var logger = LoggerFor(target);
            // Like a long stack trace: a batch of 1000 such logs is over the 10 MB ingestion takes
            var exception = new InvalidOperationException(new string('x', 12 * 1024));

            for (var i = 1; i <= 1000; i++) logger.Error(exception, "Log " + i);

            var requestSizes = new System.Collections.Generic.List<int>();
            var messages = new System.Collections.Generic.List<string>();
            while (messages.Count < 1000) {
                var request = ingestion.NextRequest();
                requestSizes.Add(System.Text.Encoding.UTF8.GetByteCount(request.Body));
                messages.AddRange(request.Logs.Select(log => (string)log["message"]));
            }

            Assert.Equal(Enumerable.Range(1, 1000).Select(i => "Log " + i), messages);
            Assert.All(requestSizes, size => Assert.InRange(size, 0, 5 * 1024 * 1024));
        }

        [Fact]
        public void DropsLogTooLargeForOneRequest()
        {
            var internalLog = CaptureInternalLog();
            var target = Target();
            target.FlushPeriodMilliseconds = 500;
            target.CaptureSourceLocation = false;
            var logger = LoggerFor(target);

            logger.Info("Before");
            logger.Log(new LogEventInfo(LogLevel.Info, "TestLogger", new string('x', 6 * 1024 * 1024)) {
                TimeStamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            });
            logger.Info("After");

            Assert.Equal(new[] { "Before", "After" }, ingestion.NextRequest().Logs.Select(log => (string)log["message"]));
            Assert.Contains("BetterStack.Logs: dropped a log of 6291631 bytes, over the limit of 5242880 bytes for a request.", internalLog.ToString());
        }

        [Fact]
        public void SendsSmallBatchInOneRequest()
        {
            var target = Target();
            target.FlushPeriodMilliseconds = 500;
            target.CaptureSourceLocation = false;
            var logger = LoggerFor(target);
            var timeStamp = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

            logger.Log(new LogEventInfo(LogLevel.Info, "TestLogger", null, "Order {orderId} placed", new object[] { 75423 }) {
                TimeStamp = timeStamp,
            });
            logger.Log(new LogEventInfo(LogLevel.Error, "TestLogger", "Payment failed") {
                TimeStamp = timeStamp,
                Exception = new InvalidOperationException("Payment gateway timed out"),
            });

            Assert.Equal(
                @"[{""dt"":""2026-01-02T03:04:05.678+00:00"",""message"":""Order 75423 placed"",""level"":""Info"",""context"":{""logger"":""TestLogger"",""properties"":{""orderId"":75423},""runtime"":{""class"":null,""member"":null,""file"":null,""line"":null}}}," +
                @"{""dt"":""2026-01-02T03:04:05.678+00:00"",""message"":""Payment failed"",""level"":""Error"",""exception"":""System.InvalidOperationException: Payment gateway timed out"",""context"":{""logger"":""TestLogger"",""properties"":{},""runtime"":{""class"":null,""member"":null,""file"":null,""line"":null}}}]",
                ingestion.NextRequest().Body);
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
            ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.Retries = 1;
            var logger = LoggerFor(target);

            logger.Info("Dropped");
            Assert.Equal("Dropped", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
            Assert.Equal("Dropped", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            logger.Info("Delivered");
            Assert.Equal("Delivered", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
        }

        [Fact]
        public void LogsTheBatchDroppedAfterRetries()
        {
            var internalLog = CaptureInternalLog();
            ingestion.StatusCodes.Enqueue(500);
            ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.Retries = 1;
            var logger = LoggerFor(target);

            logger.Info("Dropped");
            ingestion.NextRequest();
            ingestion.NextRequest();
            // The next batch goes out after the dropped one has been logged
            logger.Info("Delivered");
            ingestion.NextRequest();

            Assert.Contains("BetterStack.Logs: request failed with status 500 Internal Server Error.", internalLog.ToString());
            Assert.Contains("BetterStack.Logs: dropped 1 logs after 2 failed attempts.", internalLog.ToString());
        }

        [Fact]
        public void DoesNotRetryTheBatchRejectedAsUnauthorized()
        {
            var internalLog = CaptureInternalLog();
            ingestion.StatusCodes.Enqueue(401);
            var logger = LoggerFor(Target());

            logger.Info("Rejected");
            Assert.Equal("Rejected", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            // A retry would come after a second
            Thread.Sleep(2500);
            Assert.False(ingestion.HasRequest, "The rejected batch was sent again.");

            logger.Info("Delivered");
            Assert.Equal("Delivered", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            Assert.Contains("BetterStack.Logs: request failed with status 401 Unauthorized.", internalLog.ToString());
            Assert.Contains("BetterStack.Logs: dropped 1 logs, the request was rejected with status 401. Check the source token and the endpoint.", internalLog.ToString());
        }

        [Theory]
        [InlineData(408)]
        [InlineData(429)]
        public void RetriesTheBatchAfterRequestTimeoutOrRateLimit(int statusCode)
        {
            ingestion.StatusCodes.Enqueue(statusCode);
            LoggerFor(Target()).Info("Hello");

            var failed = ingestion.NextRequest();
            var retried = ingestion.NextRequest();
            Assert.Equal("Hello", (string)Assert.Single(failed.Logs)["message"]);
            Assert.Equal(failed.Body, retried.Body);
        }

        [Fact]
        public void SendsTheBatchOnceWhenRetriesIsZero()
        {
            var internalLog = CaptureInternalLog();
            ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.Retries = 0;
            var logger = LoggerFor(target);

            logger.Info("Dropped");
            Assert.Equal("Dropped", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            // Goes out once the dropped batch has been given up on: a retry would arrive first
            logger.Info("Delivered");
            Assert.Equal("Delivered", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);

            Assert.Contains("BetterStack.Logs: dropped 1 logs after 1 failed attempts.", internalLog.ToString());
        }

        [Fact]
        public void RetriesTheBatchAsManyTimesAsRetriesSays()
        {
            for (var i = 0; i < 3; i++) ingestion.StatusCodes.Enqueue(500);
            var target = Target();
            target.Retries = 2;
            var logger = LoggerFor(target);

            logger.Info("Dropped");
            var first = ingestion.NextRequest();
            Assert.Equal("Dropped", (string)Assert.Single(first.Logs)["message"]);
            Assert.Equal(first.Body, ingestion.NextRequest().Body);
            Assert.Equal(first.Body, ingestion.NextRequest().Body);

            // Goes out once the dropped batch has been given up on: a further attempt would arrive first
            logger.Info("Delivered");
            Assert.Equal("Delivered", (string)Assert.Single(ingestion.NextRequest().Logs)["message"]);
        }

        private System.IO.StringWriter CaptureInternalLog()
        {
            var internalLog = new System.IO.StringWriter();
            InternalLogger.LogLevel = LogLevel.Warn;
            InternalLogger.LogWriter = internalLog;
            return internalLog;
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
        public void ReportsMaxBatchSizeBelowOneAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.MaxBatchSize = 0;

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: maxBatchSize is 0. Set it to 1 or more.", exception.Message);
        }

        [Fact]
        public void ReportsFlushPeriodBelowOneMillisecondAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.FlushPeriodMilliseconds = 0;

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: flushPeriodMilliseconds is 0. Set it to 1 or more.", exception.Message);
        }

        [Fact]
        public void ReportsNegativeRetriesAsConfigurationError()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.Retries = -1;

            var exception = Assert.Throws<NLogConfigurationException>(() => LoggerFor(target));
            Assert.Equal("BetterStack.Logs: retries is -1. Set it to 0 or more.", exception.Message);
        }

        [Fact]
        public void AcceptsZeroRetries()
        {
            logFactory.ThrowConfigExceptions = true;
            var target = Target();
            target.Retries = 0;

            Assert.Null(Record.Exception(() => LoggerFor(target)));
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
