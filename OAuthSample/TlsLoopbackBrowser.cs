using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient.Browser;

namespace OAuthSample
{
    /// <summary>
    /// An OidcClient <see cref="IBrowser"/> that opens the system browser and captures
    /// the redirect on a loopback address — including <c>https</c>, via the in-process
    /// TLS listener (<see cref="LoopbackCallbackListener"/>).
    ///
    /// OidcClient ships its own loopback browser, but it uses <see cref="System.Net.HttpListener"/>
    /// on <c>http://localhost</c> — which is exactly what MLA rejects, and what http.sys
    /// won't route for <c>https</c>. Supplying this browser is all it takes to make the
    /// library work against MLA's forced <c>https://localhost</c> callback.
    /// </summary>
    public sealed class TlsLoopbackBrowser : IBrowser
    {
        private readonly Uri _redirect;
        private readonly Action<string> _log;

        public TlsLoopbackBrowser(string redirectUri, Action<string> log)
        {
            _redirect = new Uri(redirectUri, UriKind.Absolute);
            _log = log;
        }

        public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
        {
            var listener = new LoopbackCallbackListener(_log);

            // Start listening BEFORE opening the browser (WaitForCallbackAsync binds the
            // socket synchronously up to its first await), so there's no redirect race.
            // OidcClient does the token exchange itself after we return, so the page can't
            // show the user/expiry here — render the generic "complete" page.
            Task<CallbackInfo> waitForCallback = listener.WaitForCallbackAsync(
                _redirect, cancellationToken, ci => Task.FromResult(LandingPage.Complete()));

            _log("Opening browser for authorization...");
            Process.Start(new ProcessStartInfo(options.StartUrl) { UseShellExecute = true });

            try
            {
                CallbackInfo info = await waitForCallback;
                return new BrowserResult { ResultType = BrowserResultType.Success, Response = info.Url };
            }
            catch (OperationCanceledException)
            {
                return new BrowserResult { ResultType = BrowserResultType.UserCancel };
            }
            catch (Exception ex)
            {
                return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
            }
        }
    }
}
