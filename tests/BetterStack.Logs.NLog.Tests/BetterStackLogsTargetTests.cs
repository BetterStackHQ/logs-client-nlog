using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
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
