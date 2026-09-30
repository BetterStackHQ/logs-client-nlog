using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BetterStack.Logs.NLog.Tests
{
    /// <summary>
    /// Local HTTP server standing in for the Better Stack ingestion endpoint.
    /// </summary>
    internal sealed class FakeIngestion : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly BlockingCollection<Request> requests = new BlockingCollection<Request>();

        public string Endpoint { get; }

        /// <summary>
        /// Status codes for the upcoming requests, in order. Every request after those gets 202.
        /// </summary>
        public ConcurrentQueue<int> StatusCodes { get; } = new ConcurrentQueue<int>();

        public FakeIngestion()
        {
            var portFinder = new TcpListener(IPAddress.Loopback, 0);
            portFinder.Start();
            var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
            portFinder.Stop();

            Endpoint = $"http://localhost:{port}";
            listener.Prefixes.Add(Endpoint + "/");
            listener.Start();

            Task.Run(Serve);
        }

        public Request NextRequest()
        {
            Assert.True(requests.TryTake(out var request, TimeSpan.FromSeconds(30)), "No request arrived within 30 seconds.");
            return request;
        }

        public bool HasRequest => requests.Count > 0;

        public void Dispose() => listener.Close();

        private async Task Serve()
        {
            while (true) {
                HttpListenerContext context;
                try {
                    context = await listener.GetContextAsync();
                } catch (Exception) when (!listener.IsListening) {
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) {
                    body = await reader.ReadToEndAsync();
                }

                requests.Add(new Request {
                    Method = context.Request.HttpMethod,
                    Path = context.Request.Url.AbsolutePath,
                    Authorization = context.Request.Headers["Authorization"],
                    ContentType = context.Request.ContentType,
                    Expect = context.Request.Headers["Expect"],
                    Body = body,
                });

                context.Response.StatusCode = StatusCodes.TryDequeue(out var statusCode) ? statusCode : 202;
                context.Response.Close();
            }
        }

        internal sealed class Request
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public string Authorization { get; set; }
            public string ContentType { get; set; }
            public string Expect { get; set; }
            public string Body { get; set; }

            // Dates stay strings, so tests can assert on exactly what was sent
            public JArray Logs => JsonConvert.DeserializeObject<JArray>(Body, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }
    }
}
