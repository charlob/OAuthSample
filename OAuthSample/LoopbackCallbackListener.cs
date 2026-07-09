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
    /// <summary>What was captured from the OAuth redirect.</summary>
    public sealed class CallbackInfo
    {
        public string Url;
        public string Code;
        public string State;
        public string Error;
        public string ErrorDescription;
    }

    /// <summary>
    /// Waits for a single OAuth redirect on a loopback address. Handles <c>http</c> via
    /// <see cref="HttpListener"/> and <c>https</c> via in-process TLS
    /// (<see cref="TcpListener"/> + <see cref="SslStream"/>), which bypasses http.sys —
    /// the reason a raw HttpListener can't receive an HTTPS loopback callback.
    ///
    /// When the real callback arrives, <c>renderResult</c> is invoked with the browser
    /// response still open; whatever HTML it returns is sent back and the connection
    /// closes. That lets the caller finish the token exchange first, so the landing page
    /// can show the signed-in user and expiry.
    /// </summary>
    public sealed class LoopbackCallbackListener
    {
        private readonly Action<string> _log;
        private int _handled; // ensures renderResult runs at most once

        public LoopbackCallbackListener(Action<string> log)
        {
            _log = log;
        }

        public Task<CallbackInfo> WaitForCallbackAsync(
            Uri redirect, CancellationToken cancellationToken, Func<CallbackInfo, Task<string>> renderResult)
        {
            return redirect.Scheme == Uri.UriSchemeHttps
                ? WaitHttpsAsync(redirect, cancellationToken, renderResult)
                : WaitHttpAsync(redirect, cancellationToken, renderResult);
        }

        // --- http (http.sys / HttpListener) ----------------------------------------

        private async Task<CallbackInfo> WaitHttpAsync(
            Uri redirect, CancellationToken cancellationToken, Func<CallbackInfo, Task<string>> renderResult)
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

                    var info = Parse(req.Url.ToString());
                    if (info.Code == null && info.Error == null)
                    {
                        WriteHttp(ctx, 404, LandingPage.Waiting());
                        continue;
                    }

                    string html = await renderResult(info);
                    WriteHttp(ctx, info.Error != null ? 400 : 200, html);
                    return info;
                }
            }
        }

        private static void WriteHttp(HttpListenerContext ctx, int status, string html)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(html);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // Browser may have already gone; nothing useful to do.
            }
        }

        // --- https (in-process TLS) -------------------------------------------------

        private async Task<CallbackInfo> WaitHttpsAsync(
            Uri redirect, CancellationToken cancellationToken, Func<CallbackInfo, Task<string>> renderResult)
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

            var result = new TaskCompletionSource<CallbackInfo>();
            using (cancellationToken.Register(() => result.TrySetCanceled()))
            {
                foreach (var l in listeners)
                    _ = AcceptLoop(l, cert, redirect, renderResult, result);

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

        private async Task AcceptLoop(
            TcpListener l, X509Certificate2 cert, Uri redirect,
            Func<CallbackInfo, Task<string>> renderResult, TaskCompletionSource<CallbackInfo> result)
        {
            while (!result.Task.IsCompleted)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync(); }
                catch { return; }
                _ = HandleAsync(client, cert, redirect, renderResult, result);
            }
        }

        private async Task HandleAsync(
            TcpClient client, X509Certificate2 cert, Uri redirect,
            Func<CallbackInfo, Task<string>> renderResult, TaskCompletionSource<CallbackInfo> result)
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

                    var info = Parse(redirect.GetLeftPart(UriPartial.Authority) + target);
                    if (info.Code == null && info.Error == null)
                    {
                        await WriteTls(ssl, 404, LandingPage.Waiting());
                        return; // stray request (favicon, probe) — keep waiting
                    }

                    // Only the first real callback renders the result; any racing one gets a generic page.
                    if (Interlocked.Exchange(ref _handled, 1) == 1)
                    {
                        await WriteTls(ssl, 200, LandingPage.Complete());
                        return;
                    }

                    string html = await renderResult(info);
                    await WriteTls(ssl, info.Error != null ? 400 : 200, html);
                    result.TrySetResult(info);
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

        private static async Task WriteTls(SslStream ssl, int status, string html)
        {
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 400 ? "Bad Request" : "OK";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string head =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await ssl.WriteAsync(headBytes, 0, headBytes.Length);
            await ssl.WriteAsync(body, 0, body.Length);
            await ssl.FlushAsync();
        }

        // --- parsing ----------------------------------------------------------------

        private static CallbackInfo Parse(string url)
        {
            var q = ParseQuery(url);
            return new CallbackInfo
            {
                Url = url,
                Code = q.TryGetValue("code", out var c) ? c : null,
                State = q.TryGetValue("state", out var s) ? s : null,
                Error = q.TryGetValue("error", out var e) ? e : null,
                ErrorDescription = q.TryGetValue("error_description", out var d) ? d : null,
            };
        }

        private static Dictionary<string, string> ParseQuery(string url)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = url.IndexOf('?');
            if (q < 0)
                return result;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                if (pair.Length == 0)
                    continue;
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                string val = eq >= 0 ? pair.Substring(eq + 1) : "";
                result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(val);
            }
            return result;
        }
    }
}
