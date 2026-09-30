using System;
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
    }
}
