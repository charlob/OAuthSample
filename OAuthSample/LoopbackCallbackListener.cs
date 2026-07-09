using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OAuthSample
{
    /// <summary>
    /// Waits for a single OAuth redirect on a loopback address and returns the full
    /// callback URL (including <c>?code=...&amp;state=...</c>). Handles <c>http</c> via
    /// <see cref="HttpListener"/> and <c>https</c> via in-process TLS
    /// (<see cref="TcpListener"/> + <see cref="SslStream"/>), which bypasses http.sys —
    /// the reason the raw HttpListener can't receive an HTTPS loopback callback.
    ///
    /// This is the reusable piece behind <see cref="TlsLoopbackBrowser"/>, so the
    /// OidcClient library can use the same trick MainForm uses by hand.
    /// </summary>
    public sealed class LoopbackCallbackListener
    {
        private readonly Action<string> _log;

        public LoopbackCallbackListener(Action<string> log)
        {
            _log = log;
        }

        public Task<string> WaitForCallbackAsync(Uri redirect, CancellationToken cancellationToken)
        {
            return redirect.Scheme == Uri.UriSchemeHttps
                ? WaitHttpsAsync(redirect, cancellationToken)
                : WaitHttpAsync(redirect, cancellationToken);
        }

        // --- http (http.sys / HttpListener) ----------------------------------------

        private async Task<string> WaitHttpAsync(Uri redirect, CancellationToken cancellationToken)
        {
            string root = redirect.Scheme + "://" + redirect.Host + ":" + redirect.Port + "/";
            var listener = new HttpListener();
            listener.Prefixes.Add(root);
            listener.Start();
            _log("Listening on " + root);

            using (cancellationToken.Register(() => { try { listener.Stop(); } catch { /* ignore */ } }))
            using (listener)
            {
                while (true)
                {
                    var ctx = await listener.GetContextAsync();
                    var req = ctx.Request;
                    _log("  <- " + req.HttpMethod + " " + req.Url.PathAndQuery);

                    bool done = req.QueryString["code"] != null || req.QueryString["error"] != null;
                    WriteHttpListener(ctx, done);
                    if (done)
                        return req.Url.ToString();
                }
            }
        }

        private static void WriteHttpListener(HttpListenerContext ctx, bool done)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(Page(done));
            ctx.Response.StatusCode = done ? 200 : 404;
            ctx.Response.ContentType = "text/html";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }

        // --- https (in-process TLS) -------------------------------------------------

        private async Task<string> WaitHttpsAsync(Uri redirect, CancellationToken cancellationToken)
        {
            var cert = LoopbackCertificate.GetOrCreate(_log);

            var listeners = new List<TcpListener>();
            foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    var l = new TcpListener(ip, redirect.Port);
                    l.Start();
                    listeners.Add(l);
                }
                catch (SocketException ex)
                {
                    _log("  (could not bind " + ip + ":" + redirect.Port + " - " + ex.Message + ")");
                }
            }
            if (listeners.Count == 0)
                throw new InvalidOperationException("Could not bind port " + redirect.Port + " on loopback (already in use?).");

            _log("Listening (in-process TLS) on https://" + redirect.Host + ":" + redirect.Port + "/");

            var result = new TaskCompletionSource<string>();
            using (cancellationToken.Register(() => result.TrySetCanceled()))
            {
                foreach (var l in listeners)
                    _ = AcceptLoop(l, cert, redirect, result);

                try
                {
                    return await result.Task;
                }
                finally
                {
                    foreach (var l in listeners)
                    {
                        try { l.Stop(); } catch { /* ignore */ }
                    }
                }
            }
        }

        private async Task AcceptLoop(TcpListener l, X509Certificate2 cert, Uri redirect, TaskCompletionSource<string> result)
        {
            while (!result.Task.IsCompleted)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync(); }
                catch { return; }
                _ = HandleAsync(client, cert, redirect, result);
            }
        }

        private async Task HandleAsync(TcpClient client, X509Certificate2 cert, Uri redirect, TaskCompletionSource<string> result)
        {
            try
            {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    await ssl.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12, false);

                    string requestLine = await ReadRequestLineAsync(ssl);
                    if (requestLine == null)
                        return; // idle/preconnect socket

                    var parts = requestLine.Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "?";
                    string target = parts.Length > 1 ? parts[1] : "/";
                    _log("  <- " + method + " " + target);

                    bool done = target.IndexOf("code=", StringComparison.Ordinal) >= 0
                             || target.IndexOf("error=", StringComparison.Ordinal) >= 0;
                    await WriteTlsAsync(ssl, done);

                    if (done)
                        result.TrySetResult(redirect.GetLeftPart(UriPartial.Authority) + target);
                }
            }
            catch (Exception ex)
            {
                _log("  (connection error: " + ex.Message + ")");
            }
        }

        private static async Task<string> ReadRequestLineAsync(SslStream ssl)
        {
            var buffer = new byte[8192];
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await ssl.ReadAsync(buffer, total, buffer.Length - total);
                if (n <= 0)
                    break;
                total += n;
                for (int i = 0; i < total; i++)
                {
                    if (buffer[i] == (byte)'\n')
                        return Encoding.ASCII.GetString(buffer, 0, i).TrimEnd('\r');
                }
            }
            return null;
        }

        private static async Task WriteTlsAsync(SslStream ssl, bool done)
        {
            byte[] body = Encoding.UTF8.GetBytes(Page(done));
            string head =
                "HTTP/1.1 " + (done ? "200 OK" : "404 Not Found") + "\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await ssl.WriteAsync(headBytes, 0, headBytes.Length);
            await ssl.WriteAsync(body, 0, body.Length);
            await ssl.FlushAsync();
        }

        private static string Page(bool done)
        {
            string html = done
                ? "<h2>Authentication complete</h2><p>You can close this window and return to the app.</p>"
                : "Waiting for OAuth callback...";
            return "<html><body style='font-family:Segoe UI,Arial,sans-serif'>" + html + "</body></html>";
        }
    }
}
