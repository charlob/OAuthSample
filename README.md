# OAuthSample

A tiny **.NET Framework 4.8 WinForms** harness that runs the OAuth 2.0
**Authorization Code + PKCE** flow against an OpenID Connect / Auth0-style
identity provider (built to test **MLA SSO**, `https://sso-s.mla.com.au`).

It exists to make one thing obvious: **where the callback is captured**. Every
request that hits the loopback listener is logged, so you can watch the `?code=`
arrive instead of guessing why it didn't.

## Using it

1. Open `OAuthSample.sln` in Visual Studio and run (F5), or build the
   `OAuthSample` project.
2. Fill in:
   - **Authority** – the IdP base URL (pre-filled with MLA SSO).
   - **Client ID** – your application's client id.
   - **Callback URI** – must **exactly match** a redirect URI registered on the
     IdP application (e.g. `https://localhost:5021/callback/envd/`). Both `http`
     and `https` loopback URIs work — see the capture notes below.
   - **Scope** – defaults to `openid profile email`.
   - **Audience** – optional; set it (to your API identifier) if you need an
     Auth0 **access token** for a specific API rather than just an id token.
3. Click **Connect**. Your browser opens for sign-in/consent; after approval the
   app captures the `code`, exchanges it at `/oauth/token`, and logs the tokens.

## How the callback capture is done (and why the usual bug is avoided)

- **Authorization Code flow, not implicit.** The result comes back as
  `...?code=xxx` in the **query string**, which the browser *does* send to the
  server. Implicit flow returns `#access_token=...` in the URL **fragment**,
  which the browser **never** sends — so a server-side `HttpListener` can never
  see it. That fragment trap is the most common reason a listener "doesn't
  capture" the response.
- **Listen on the loopback root**, e.g. `http://localhost:5000/`, and inspect
  the path/query ourselves. `HttpListener` prefix matching is picky about
  trailing slashes (`/callback` vs `/callback/`), and a mismatched prefix is the
  other common reason the listener silently never fires.
- **Stray requests are ignored.** A browser often fires `GET /favicon.ico`; the
  listener loops and only completes on the request that actually carries `code`.
- **PKCE (S256)** and a **`state`** check are included; `state` is verified to
  guard against CSRF.
- **TLS 1.2/1.3** is pinned in `Program.cs` because .NET Framework 4.8 can
  otherwise negotiate an older protocol the IdP rejects.

## HTTP vs HTTPS callbacks (this matters a lot on Windows)

The scheme of the **Callback URI** decides how the app captures the redirect:

- **`http://localhost:port/...`** → captured with `HttpListener` (via http.sys).
  Simple and reliable. This is the [RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252#section-7.3)-recommended
  redirect for native/desktop apps. Use it whenever the IdP lets you register an
  `http://localhost` callback.

- **`https://localhost:port/...`** → captured with a raw `TcpListener` +
  `SslStream`, terminating TLS **in-process**. This is deliberate: `HttpListener`
  relies on **http.sys**, and http.sys will **not** route an HTTPS request to an
  `HttpListener` on loopback — even with a certificate bound via
  `netsh http add sslcert`, it completes the TLS handshake and then returns
  **HTTP 503** (the request never reaches your listener; `Requests arrived: 0`).
  Terminating TLS ourselves sidesteps http.sys entirely, so there is **no**
  `netsh` sslcert binding, **no** `urlacl`, and **no** 503.

  The app **provisions its own certificate**: on first use it creates a
  self-signed cert for `localhost` (SAN: `localhost`, `127.0.0.1`, `::1`) in the
  current user's store and adds it to the user's Trusted Root — Windows shows a
  one-time trust dialog; click **Yes**. No admin rights required. Subsequent runs
  reuse it.

Use `https` only when the IdP forces it (e.g. the client is registered with an
`https://localhost` callback that you can't change). Otherwise prefer `http`.

## Notes

- Pure Framework assemblies only — no NuGet packages. JSON is pretty-printed via
  `System.Web.Extensions`' `JavaScriptSerializer`; the self-signed cert is built
  with the framework's `CertificateRequest` API.
- Loopback (`localhost`) prefixes need no admin rights. A non-localhost prefix
  would require a `netsh http add urlacl` reservation.
- If you previously bound a cert for testing, you can remove it — it's no longer
  used: `netsh http delete sslcert ipport=0.0.0.0:5021` and
  `netsh http delete urlacl url=https://+:5021/`.
