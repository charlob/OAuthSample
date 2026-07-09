using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>
    /// Minimal WinForms harness that walks the OAuth 2.0 Authorization Code + PKCE
    /// flow end to end and logs every step, so you can watch exactly where a
    /// callback is (or isn't) being captured.
    ///
    /// HTTP callbacks are captured with <see cref="HttpListener"/> (simple, uses
    /// http.sys). HTTPS callbacks are captured with a raw <see cref="TcpListener"/>
    /// + <see cref="SslStream"/> so TLS is terminated IN-PROCESS — this deliberately
    /// bypasses http.sys, which will not route an HTTPS request to an HttpListener on
    /// loopback (it returns 503 even with an sslcert bound). The app provisions and
    /// trusts its own loopback certificate, so no netsh sslcert / urlacl is needed.
    /// </summary>
    public sealed class MainForm : Form
    {
        // Shared HttpClient for the token exchange.
        private static readonly HttpClient Http = new HttpClient();

        private readonly TextBox _txtAuthority;
        private readonly TextBox _txtClientId;
        private readonly TextBox _txtRedirect;
        private readonly TextBox _txtScope;
        private readonly TextBox _txtAudience;
        private readonly Button _btnConnect;
        private readonly TextBox _txtLog;

        public MainForm()
        {
            Text = "OAuth 2.0 Sample (Authorization Code + PKCE)";
            Width = 900;
            Height = 640;
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
                AutoSize = false,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _txtAuthority = AddRow(layout, "Authority", "https://sso-s.mla.com.au");
            _txtClientId = AddRow(layout, "Client ID", "");
            _txtRedirect = AddRow(layout, "Callback URI", "https://localhost:5021/callback/envd/");
            _txtScope = AddRow(layout, "Scope", "openid profile email");
            _txtAudience = AddRow(layout, "Audience (opt.)", "");

            _btnConnect = new Button
            {
                Text = "Connect",
                AutoSize = true,
                Padding = new Padding(16, 4, 16, 4),
                Anchor = AnchorStyles.Left,
            };
            _btnConnect.Click += OnConnectClick;
            layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, layout.RowCount);
            layout.Controls.Add(_btnConnect, 1, layout.RowCount);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
            };
            layout.Controls.Add(_txtLog, 0, layout.RowCount);
            layout.SetColumnSpan(_txtLog, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowCount++;

            Log("Ready. Fill in Client ID (and adjust Callback URI to match what's");
            Log("registered at the IdP), then click Connect.");
        }

        private static TextBox AddRow(TableLayoutPanel layout, string label, string value)
        {
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 3),
            };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill };
            int row = layout.RowCount;
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(box, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;
            return box;
        }

        private async void OnConnectClick(object sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            try
            {
                await RunFlowAsync();
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
            finally
            {
                _btnConnect.Enabled = true;
            }
        }

        private async Task RunFlowAsync()
        {
            string authority = _txtAuthority.Text.Trim().TrimEnd('/');
            string clientId = _txtClientId.Text.Trim();
            string redirectUri = _txtRedirect.Text.Trim();
            string scope = _txtScope.Text.Trim();
            string audience = _txtAudience.Text.Trim();

            if (string.IsNullOrEmpty(clientId))
            {
                Log("Please enter a Client ID.");
                return;
            }
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect))
            {
                Log("Callback URI is not a valid absolute URL.");
                return;
            }

            string state = RandomUrlToken(16);
            string codeVerifier = RandomUrlToken(32);
            string codeChallenge = Base64Url(Sha256(codeVerifier));

            var authorizeUrl = new StringBuilder();
            authorizeUrl.Append(authority).Append("/authorize?");
            authorizeUrl.Append("response_type=code");
            authorizeUrl.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
            authorizeUrl.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            authorizeUrl.Append("&scope=").Append(Uri.EscapeDataString(scope));
            authorizeUrl.Append("&state=").Append(Uri.EscapeDataString(state));
            authorizeUrl.Append("&code_challenge=").Append(codeChallenge);
            authorizeUrl.Append("&code_challenge_method=S256");
            if (!string.IsNullOrEmpty(audience))
                authorizeUrl.Append("&audience=").Append(Uri.EscapeDataString(audience));

            Log("");
            Log("  authorize: " + authority + "/authorize");
            Log("  state:     " + state);
            Log("  challenge: " + codeChallenge + " (S256)");

            string code = redirect.Scheme == Uri.UriSchemeHttps
                ? await CaptureViaTlsAsync(redirect, state, authorizeUrl.ToString())
                : await CaptureViaHttpListenerAsync(redirect, state, authorizeUrl.ToString());

            if (code == null)
                return; // reason already logged

            Log("");
            Log("Authorization code captured. Exchanging for tokens...");
            await ExchangeCodeAsync(authority, clientId, redirectUri, code, codeVerifier);
        }

        // --- HTTP capture (http.sys / HttpListener) --------------------------------

        private async Task<string> CaptureViaHttpListenerAsync(Uri redirect, string expectedState, string authorizeUrl)
        {
            // Listen on the loopback ROOT and inspect the path ourselves — HttpListener
            // prefix matching is fussy about trailing slashes, and a missed match is the
            // classic "listener never fires" bug.
            string rootPrefix = redirect.Scheme + "://" + redirect.Host + ":" + redirect.Port + "/";

            var listener = new HttpListener();
            listener.Prefixes.Add(rootPrefix);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Log("Could not start listener on " + rootPrefix);
                Log("  " + ex.Message);
                Log("  (A conflict means another instance already owns the port.)");
                return null;
            }

            using (listener)
            {
                Log("Listening on " + rootPrefix);
                Log("Opening browser for authorization...");
                LaunchBrowser(authorizeUrl);

                while (true)
                {
                    var ctx = await listener.GetContextAsync();
                    var req = ctx.Request;
                    Log("  <- " + req.HttpMethod + " " + req.Url.PathAndQuery);

                    string code = req.QueryString["code"];
                    string error = req.QueryString["error"];
                    string returnedState = req.QueryString["state"];

                    if (error != null)
                    {
                        Log("  Authorization error: " + error + " - " + req.QueryString["error_description"]);
                        RespondHttpListener(ctx, 400, "Authorization failed: " + WebUtility.HtmlEncode(error));
                        return null;
                    }
                    if (code == null)
                    {
                        RespondHttpListener(ctx, 404, "Waiting for OAuth callback...");
                        continue;
                    }
                    if (returnedState != expectedState)
                    {
                        Log("  WARNING: state mismatch (possible CSRF). Expected " + expectedState + " got " + returnedState);
                        RespondHttpListener(ctx, 400, "State mismatch - aborting.");
                        return null;
                    }

                    RespondHttpListener(ctx, 200,
                        "<h2>Authentication complete</h2><p>You can close this window and return to the app.</p>");
                    return code;
                }
            }
        }

        private static void RespondHttpListener(HttpListenerContext ctx, int status, string html)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(
                    "<html><body style='font-family:Segoe UI,Arial,sans-serif'>" + html + "</body></html>");
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // Browser may have already gone; nothing useful to do.
            }
        }

        // --- HTTPS capture (in-process TLS, bypasses http.sys) ---------------------

        private async Task<string> CaptureViaTlsAsync(Uri redirect, string expectedState, string authorizeUrl)
        {
            Log("HTTPS callback -> terminating TLS in-process (bypassing http.sys).");

            X509Certificate2 cert;
            try
            {
                cert = EnsureLoopbackCertificate();
            }
            catch (Exception ex)
            {
                Log("Could not provision a TLS certificate: " + ex.Message);
                return null;
            }

            // Bind both loopback addresses; the browser may resolve "localhost" to
            // either 127.0.0.1 or ::1.
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
                    Log("  (could not bind " + ip + ":" + redirect.Port + " - " + ex.Message + ")");
                }
            }
            if (listeners.Count == 0)
            {
                Log("Could not bind port " + redirect.Port + " on loopback — is another instance already running?");
                return null;
            }

            Log("Listening (in-process TLS) on https://" + redirect.Host + ":" + redirect.Port + "/");
            Log("Opening browser for authorization...");
            LaunchBrowser(authorizeUrl);

            var result = new TaskCompletionSource<string>();
            var stop = new CancellationTokenSource();

            async Task AcceptLoop(TcpListener l)
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await l.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = HandleClientAsync(client, cert, expectedState, result);
                }
            }

            foreach (var l in listeners)
                _ = AcceptLoop(l);

            try
            {
                return await result.Task;
            }
            finally
            {
                stop.Cancel();
                foreach (var l in listeners)
                {
                    try { l.Stop(); } catch { /* ignore */ }
                }
            }
        }

        private async Task HandleClientAsync(
            TcpClient client, X509Certificate2 cert, string expectedState, TaskCompletionSource<string> result)
        {
            try
            {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12, false);
                    }
                    catch (Exception ex)
                    {
                        Log("  TLS handshake failed: " + ex.Message);
                        return;
                    }

                    string requestLine = await ReadRequestLineAsync(ssl);
                    if (requestLine == null)
                        return; // idle/preconnect socket, no request

                    var parts = requestLine.Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "?";
                    string target = parts.Length > 1 ? parts[1] : "/";
                    Log("  <- " + method + " " + target);

                    var q = ParseQuery(target);
                    string error = q.ContainsKey("error") ? q["error"] : null;
                    string code = q.ContainsKey("code") ? q["code"] : null;
                    string returnedState = q.ContainsKey("state") ? q["state"] : null;

                    if (error != null)
                    {
                        Log("  Authorization error: " + error + " - " + (q.ContainsKey("error_description") ? q["error_description"] : ""));
                        await WriteTlsResponseAsync(ssl, 400, "Authorization failed: " + WebUtility.HtmlEncode(error));
                        result.TrySetResult(null);
                        return;
                    }
                    if (code == null)
                    {
                        await WriteTlsResponseAsync(ssl, 404, "Waiting for OAuth callback...");
                        return; // stray request (favicon, probe) — keep waiting
                    }
                    if (returnedState != expectedState)
                    {
                        Log("  WARNING: state mismatch (possible CSRF). Expected " + expectedState + " got " + returnedState);
                        await WriteTlsResponseAsync(ssl, 400, "State mismatch - aborting.");
                        result.TrySetResult(null);
                        return;
                    }

                    await WriteTlsResponseAsync(ssl, 200,
                        "<h2>Authentication complete</h2><p>You can close this window and return to the app.</p>");
                    result.TrySetResult(code);
                }
            }
            catch (Exception ex)
            {
                Log("  (connection error: " + ex.Message + ")");
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

        private static async Task WriteTlsResponseAsync(SslStream ssl, int status, string bodyHtml)
        {
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 400 ? "Bad Request" : "OK";
            byte[] body = Encoding.UTF8.GetBytes(
                "<html><body style='font-family:Segoe UI,Arial,sans-serif'>" + bodyHtml + "</body></html>");
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

        /// <summary>
        /// Finds (or creates and trusts) a self-signed certificate for localhost in the
        /// current user's store. No admin rights or netsh needed. The first time it
        /// creates one, Windows shows a trust-prompt dialog — click Yes.
        /// </summary>
        private X509Certificate2 EnsureLoopbackCertificate()
        {
            const string subject = "CN=OAuthSample Loopback";

            using (var my = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                my.Open(OpenFlags.ReadWrite);

                foreach (var existing in my.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, false))
                {
                    if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(1))
                    {
                        Log("Using existing loopback certificate (" + subject + ").");
                        return existing;
                    }
                }

                Log("Creating a self-signed loopback certificate (" + subject + ")...");
                using (var rsa = RSA.Create(2048))
                {
                    var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                    var san = new SubjectAlternativeNameBuilder();
                    san.AddDnsName("localhost");
                    san.AddIpAddress(IPAddress.Loopback);
                    san.AddIpAddress(IPAddress.IPv6Loopback);
                    req.CertificateExtensions.Add(san.Build());
                    req.CertificateExtensions.Add(
                        new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

                    using (var ephemeral = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2)))
                    {
                        // Re-import via PFX so the private key is persisted and usable by SslStream.
                        var cert = new X509Certificate2(
                            ephemeral.Export(X509ContentType.Pfx),
                            (string)null,
                            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

                        my.Add(cert);
                        using (var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                        {
                            root.Open(OpenFlags.ReadWrite);
                            root.Add(cert); // one-time Windows trust prompt -> click Yes
                        }

                        Log("Certificate created and trusted for the current user.");
                        Log("(If Windows shows a security-warning dialog, click Yes to trust it.)");
                        return cert;
                    }
                }
            }
        }

        // --- token exchange --------------------------------------------------------

        private async Task ExchangeCodeAsync(
            string authority, string clientId, string redirectUri, string code, string codeVerifier)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            });

            string tokenUrl = authority + "/oauth/token";
            Log("  POST " + tokenUrl);

            HttpResponseMessage resp = await Http.PostAsync(tokenUrl, form);
            string body = await resp.Content.ReadAsStringAsync();

            Log("  HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                Log("  Token endpoint returned an error:");
                Log("  " + body);
                return;
            }

            Log("");
            Log("=== Token response ===");
            try
            {
                var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                foreach (var kvp in map)
                {
                    string val = kvp.Value?.ToString() ?? "";
                    if ((kvp.Key.EndsWith("_token") || kvp.Key == "id_token") && val.Length > 24)
                        val = val.Substring(0, 24) + "... (" + val.Length + " chars)";
                    Log("  " + kvp.Key + ": " + val);
                }
            }
            catch
            {
                Log(body);
            }
            Log("======================");
        }

        // --- helpers ---------------------------------------------------------------

        private void LaunchBrowser(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static Dictionary<string, string> ParseQuery(string target)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = target.IndexOf('?');
            if (q < 0)
                return result;
            foreach (var pair in target.Substring(q + 1).Split('&'))
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

        private static byte[] Sha256(string input)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.ASCII.GetBytes(input));
        }

        private static string RandomUrlToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(buffer);
            return Base64Url(buffer);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private void Log(string message)
        {
            if (_txtLog.InvokeRequired)
            {
                _txtLog.BeginInvoke(new Action<string>(Log), message);
                return;
            }
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }
    }
}
