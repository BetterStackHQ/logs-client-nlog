using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BetterStack.Logs.NLog.Tests
{
    /// <summary>
    /// Plain TCP server that answers http requests with 202 and leaves their connections open, so a test can see
    /// when the client closes them.
    /// </summary>
    internal sealed class KeepAliveServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);

        public string Endpoint { get; }

        public KeepAliveServer()
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        }

        /// <summary>
        /// Accepts the next connection, answers one request on it and returns the connection, still open.
        /// </summary>
        public NetworkStream AnswerRequest()
        {
            var accept = listener.AcceptTcpClientAsync();
            Assert.True(accept.Wait(TimeSpan.FromSeconds(30)), "No connection arrived within 30 seconds.");
            var stream = accept.Result.GetStream();
            // Plenty for a disposed client to close the connection, and well below the idle timeout of one that was not
            stream.ReadTimeout = 10000;

            // The body is ASCII JSON, so its Content-Length in bytes is its length in characters
            var reader = new StreamReader(stream, Encoding.ASCII);
            var contentLength = 0;
            string header;
            while ((header = reader.ReadLine()) != "") {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(header.Substring(15).Trim());
            }
            reader.ReadBlock(new char[contentLength], 0, contentLength);

            var response = Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\n\r\n");
            stream.Write(response, 0, response.Length);
            return stream;
        }

        public void Dispose() => listener.Stop();
    }
}
