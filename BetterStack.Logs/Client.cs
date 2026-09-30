using System;
using System.Collections.Generic;
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
    public sealed class Client : IDisposable
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
        /// Releases the HTTP client and its connections. Nothing can be sent afterwards.
        /// </summary>
        public void Dispose()
        {
            httpClient.Dispose();
        }

        /// <summary>
        /// Sends a collection of logs to the server with several retries
        /// if an error occures.
        /// </summary>
        public async Task Send(IEnumerable<Log> logs)
        {
            var payload = serialize(logs);

            for (int i = 0; i < retries; ++i) {
                await Task.Delay(TimeSpan.FromSeconds(i));

                var success = await sendOnce(payload);
                if (success) break;
            }
        }

        private async Task<bool> sendOnce(byte[] payload)
        {
            try {
                // Every attempt needs its own HttpContent. On .NET Framework, HttpClient disposes
                // the request content as soon as the request completes -- on success, on 5xx, on
                // timeout and on network error alike -- so reusing one instance makes every retry
                // after the first throw ObjectDisposedException instead of reaching the server.
                using (var content = buildContent(payload))
                using (var response = await httpClient.PostAsync("/", content)) {
                    return response.IsSuccessStatusCode;
                }
            } catch (TaskCanceledException ex) {
                // request timed out
                global::NLog.Common.InternalLogger.Warn(ex, "BetterStack.Logs: request timed out.");
            } catch (HttpRequestException ex) {
                // TODO: repeat only for certain HTTP errors (429, 5xx)
                // some networking error
                global::NLog.Common.InternalLogger.Warn(ex, "BetterStack.Logs: request failed.");
            } catch (Exception ex) {
                // An unexpected exception must never escape: it would fault the Drain's delivery
                // task and silently stop all logging for the lifetime of the process.
                global::NLog.Common.InternalLogger.Error(ex, "BetterStack.Logs: unexpected error while sending logs.");
            }

            return false;
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
