using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BetterStack.Logs
{
    /// <summary>
    /// The Client class is responsible for reliable delivery of logs to the Better Stack servers.
    /// </summary>
    public sealed class Client
    {
        private readonly HttpClient httpClient;
        private readonly JsonSerializerSettings settings = new JsonSerializerSettings {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            ContractResolver = new DefaultContractResolver {
                NamingStrategy = new CamelCaseNamingStrategy()
            }
        };
        private readonly int retries;

        public Client(
            string sourceToken,
            string endpoint = "https://in.logs.betterstack.com",
            TimeSpan? timeout = null,
            int retries = 10
        )
        {
            settings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
            settings.Converters.Add(new ToStringJsonConverter(typeof(System.Reflection.MemberInfo)));
            settings.Converters.Add(new ToStringJsonConverter(typeof(System.Reflection.Assembly)));
            settings.Converters.Add(new ToStringJsonConverter(typeof(System.Reflection.Module)));
            settings.Error = (sender, args) =>
            {
                args.ErrorContext.Handled = true;   // Ignore Properties that throws Exceptions
            };

            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {sourceToken}");
            httpClient.BaseAddress = new Uri(endpoint);
            httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(10);

            this.retries = retries;
        }

        /// <summary>
        /// Sends a collection of logs to the server with several retries
        /// if an error occures.
        /// </summary>
        public async Task Send(IEnumerable<Log> logs)
        {
            var count = logs.Count();
            var payload = serialize(logs);

            // retries counts the attempts after the first one
            for (int i = 0; i <= retries; ++i) {
                await Task.Delay(TimeSpan.FromSeconds(i));

                var statusCode = await sendOnce(payload);
                if (statusCode >= 200 && statusCode <= 299) return;

                // Worth another attempt: no response at all, a request timeout, rate limiting or a server error
                if (statusCode == null || statusCode == 408 || statusCode == 429 || statusCode >= 500) continue;

                // Any other status is the final answer to this request: sending it again would get
                // the same one and only hold up the logs queued behind it.
                var hint = statusCode == 401 || statusCode == 403 ? " Check the source token and the endpoint." : "";
                global::NLog.Common.InternalLogger.Error("BetterStack.Logs: dropped {0} logs, the request was rejected with status {1}.{2}", count, statusCode.Value, hint);
                return;
            }

            global::NLog.Common.InternalLogger.Error("BetterStack.Logs: dropped {0} logs after {1} failed attempts.", count, retries + 1);
        }

        /// <summary>
        /// Returns the status code of the response, or null when there was none.
        /// </summary>
        private async Task<int?> sendOnce(byte[] payload)
        {
            try {
                // Every attempt needs its own HttpContent. On .NET Framework, HttpClient disposes
                // the request content as soon as the request completes -- on success, on 5xx, on
                // timeout and on network error alike -- so reusing one instance makes every retry
                // after the first throw ObjectDisposedException instead of reaching the server.
                using (var content = buildContent(payload))
                using (var response = await httpClient.PostAsync("/", content)) {
                    if (!response.IsSuccessStatusCode) {
                        global::NLog.Common.InternalLogger.Warn("BetterStack.Logs: request failed with status {0} {1}.", (int)response.StatusCode, response.ReasonPhrase);
                    }
                    return (int)response.StatusCode;
                }
            } catch (TaskCanceledException ex) {
                // request timed out
                global::NLog.Common.InternalLogger.Warn(ex, "BetterStack.Logs: request timed out.");
            } catch (HttpRequestException ex) {
                // some networking error
                global::NLog.Common.InternalLogger.Warn(ex, "BetterStack.Logs: request failed.");
            } catch (Exception ex) {
                // An unexpected exception must never escape: it would fault the Drain's delivery
                // task and silently stop all logging for the lifetime of the process.
                global::NLog.Common.InternalLogger.Error(ex, "BetterStack.Logs: unexpected error while sending logs.");
            }

            return null;
        }

        private byte[] serialize(IEnumerable<Log> logs) {
            var payload = JsonConvert.SerializeObject(logs, settings);
            return Encoding.UTF8.GetBytes(payload);
        }

        private HttpContent buildContent(byte[] payload) {
            var content = new ByteArrayContent(payload);
            content.Headers.Add("Content-Type", "application/json");
            return content;
        }

        /// <summary>
        /// JSON converter that just calls ToString on the target value (when non-null).
        /// This is configured as the converter for types that will otherwise spew a lot of irrelevant JSON
        /// into logs.
        /// </summary>
        internal sealed class ToStringJsonConverter : JsonConverter
        {
            private readonly System.Type _type;

            /// <inheritdoc />
            public override bool CanRead => false;

            public ToStringJsonConverter(System.Type type) =>
                _type = type;

            /// <inheritdoc />
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value is null)
                {
                    writer.WriteNull();
                }
                else
                {
                    writer.WriteValue(value.ToString());
                }
            }

            /// <inheritdoc />
            public override object ReadJson(JsonReader reader, System.Type objectType, object existingValue, JsonSerializer serializer) =>
                throw new NotSupportedException("Only serialization is supported");

            /// <inheritdoc />
            public override bool CanConvert(System.Type objectType) =>
                _type.IsAssignableFrom(objectType);
        }
    }
}
