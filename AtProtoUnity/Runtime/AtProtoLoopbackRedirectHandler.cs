using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace AtProtoUnity
{
    public sealed class LoopbackAtProtoOAuthRedirectHandlerFactory : IAtProtoOAuthRedirectHandlerFactory
    {
        public IAtProtoOAuthRedirectHandler Create()
        {
            return new LoopbackAtProtoOAuthRedirectHandler();
        }
    }

    // Desktop redirect handler; the listener only binds to the loopback interface.
    public sealed class LoopbackAtProtoOAuthRedirectHandler : IAtProtoOAuthRedirectHandler, IAtProtoOAuthAuthorizationLauncher
    {
        readonly TcpListener listener;
        readonly Thread listenerThread;
        readonly object sync = new();
        AtProtoOAuthCallback callback;
        bool disposed;

        public string RedirectUri { get; }

        public LoopbackAtProtoOAuthRedirectHandler()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            RedirectUri = "http://127.0.0.1:" + endpoint.Port + "/callback";
            listenerThread = new Thread(Listen)
            {
                IsBackground = true,
                Name = "AT Protocol OAuth callback"
            };
            listenerThread.Start();
        }

        public async Awaitable<AtProtoOAuthCallback> WaitForCallbackAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (sync)
                {
                    if (callback != null)
                        return callback;
                    if (disposed)
                        throw new OperationCanceledException();
                }

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("The OAuth callback was not received before the timeout.");

                await Awaitable.WaitForSecondsAsync(0.05f, cancellationToken);
            }
        }

        public void Dispose()
        {
            lock (sync)
                disposed = true;
            listener.Stop();
        }

        public void OpenAuthorizationUrl(string url)
        {
            Application.OpenURL(url);
        }

        void Listen()
        {
            try
            {
                while (true)
                {
                    var tcpClient = listener.AcceptTcpClient();
                    try
                    {
                        using (tcpClient)
                            HandleClient(tcpClient);
                    }
                    catch (IOException)
                    {
                        // A dropped connection must not stop the listener.
                    }

                    lock (sync)
                    {
                        if (disposed || callback != null)
                            return;
                    }
                }
            }
            catch (SocketException)
            {
                // Stopping the listener is the normal shutdown path.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void HandleClient(TcpClient tcpClient)
        {
            using (var stream = tcpClient.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                var requestLine = reader.ReadLine();
                if (requestLine == null)
                    return;

                while (!string.IsNullOrEmpty(reader.ReadLine()))
                {
                }

                var requestParts = requestLine.Split(' ');
                if (requestParts.Length < 2)
                {
                    WriteResponse(stream, 400, ErrorPage("Invalid callback request."));
                    return;
                }

                AtProtoOAuthCallback received;
                try
                {
                    received = ParseCallback(requestParts[1]);
                }
                catch (Exception exception)
                {
                    WriteResponse(stream, 400, ErrorPage(exception.Message));
                    return;
                }

                lock (sync)
                {
                    if (!disposed && callback == null)
                        callback = received;
                }
                WriteResponse(stream, 200, SuccessPage);
            }
        }

        AtProtoOAuthCallback ParseCallback(string target)
        {
            var callbackUri = new Uri(new Uri(RedirectUri), target);
            if (!string.Equals(callbackUri.AbsolutePath, "/callback", StringComparison.Ordinal))
                throw new InvalidOperationException("The OAuth callback path was not recognized.");

            var query = callbackUri.Query.TrimStart('?').Split('&');
            string code = null;
            string state = null;
            string issuer = null;
            string error = null;
            string errorDescription = null;
            foreach (var item in query)
            {
                if (string.IsNullOrEmpty(item))
                    continue;
                var separator = item.IndexOf('=');
                var key = DecodeQueryValue(separator < 0 ? item : item.Substring(0, separator));
                var value = separator < 0
                    ? string.Empty
                    : DecodeQueryValue(item.Substring(separator + 1));
                switch (key)
                {
                    case "code": code = value; break;
                    case "state": state = value; break;
                    case "iss": issuer = value; break;
                    case "error": error = value; break;
                    case "error_description": errorDescription = value; break;
                }
            }

            return new AtProtoOAuthCallback(code, state, error, errorDescription, issuer);
        }

        static string DecodeQueryValue(string value)
        {
            return Uri.UnescapeDataString((value ?? string.Empty).Replace('+', ' '));
        }

        static string ErrorPage(string message)
        {
            return "<html><body>" + WebUtility.HtmlEncode(message) + "</body></html>";
        }

        const string SuccessPage =
            "<!doctype html><html><body><p id=\"message\">Authentication complete. Closing this tab...</p>"
            + "<script>window.close();"
            + "setTimeout(function(){document.getElementById('message').textContent="
            + "'Authentication complete. You can close this tab.';},500);</script></body></html>";

        static void WriteResponse(NetworkStream stream, int statusCode, string html)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(html);
            var header = "HTTP/1.1 " + statusCode + (statusCode == 200 ? " OK" : " Bad Request") + "\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: " + bodyBytes.Length + "\r\n"
                + "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }
    }
}