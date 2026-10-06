# Nowify

A Spotify "Now Playing" display for Raspberry Pi, built with **Blazor Server**
(.NET 10, Interactive Server rendering). Spotify playback can happen on any device
on your account; the Pi only needs a browser to display Nowify.

## Features

- Album cover, track title, and comma-separated artists.
- Responsive, full-screen layout and artwork-derived background/text colours.
- Idle screen when playback is paused or no device session is active.
- Spotify authorization with the `user-read-currently-playing` scope only.
- Server-side access/refresh tokens, automatic renewal, and remembered login.
- Sequential polling every 2.5 seconds, with rate-limit backoff and transient-error status.
- Logout with session revocation and CSRF protection.

Original display previews:
![Nowify Preview Image 1](assets/preview-1.png?raw=true "Nowify preview image, cover art for the song 'Wherever you go' by The Avalanches and Jamie xx")
![Nowify Preview Image 2](assets/preview-2.png?raw=true "Nowify preview image, cover art for the song 'Gas Drawls' by MF DOOM")
![Nowify Preview Image 3](assets/preview-3.png?raw=true "Nowify preview image, cover art for the song '有吗炒面' by Lexie Liu")

## Local setup

Install the **.NET 10 SDK**. Node, Yarn, and the Vue toolchain are no longer needed.

1. Create an application in the [Spotify developer dashboard](https://developer.spotify.com/dashboard).
2. Register `http://127.0.0.1:5267/signin-spotify` as its redirect URI.
   The scheme, host, port, and callback path must match exactly. Use a loopback IP
   for HTTP development; deployed non-loopback redirects require HTTPS.
3. Configure credentials using .NET user secrets:

   ```sh
   dotnet user-secrets set "Spotify:ClientId" "YOUR_CLIENT_ID"
   dotnet user-secrets set "Spotify:ClientSecret" "YOUR_CLIENT_SECRET"
   ```

4. Start the server:

   ```sh
   dotnet run --no-launch-profile --urls http://127.0.0.1:5267 --environment Development
   ```

5. Open `http://127.0.0.1:5267`, choose **Login with Spotify**, and start playback.

Alternatively, supply `Spotify__ClientId` and `Spotify__ClientSecret` as server
environment variables. Do not put real credentials in `appsettings.json`, source
control, or browser JavaScript. The old `VUE_APP_SP_*` variables are not used.
Local `.env` files are not automatically loaded.

## Hosting and Raspberry Pi

**Blazor Server requires a running ASP.NET Core server and a persistent SignalR
connection. Static hosts such as GitHub Pages or a static Netlify deployment are
not sufficient.** Host on an ASP.NET Core-capable service, Linux server, or Pi.
The Pi's browser may connect to a server running on another machine.

Publish and run with the .NET 10 ASP.NET Core runtime:

```sh
dotnet publish -c Release -o /tmp/nowify-publish
dotnet /tmp/nowify-publish/Nowify.dll --urls http://127.0.0.1:5267
```

For a self-contained 64-bit Raspberry Pi OS deployment:

```sh
dotnet publish -c Release -r linux-arm64 --self-contained true -o /tmp/nowify-publish
```

Use `linux-arm` instead for a supported 32-bit OS. Copy the published output to
the target and run `./Nowify`. Configure the server to start automatically with
your host's service manager, then open its URL in the Pi browser's kiosk/full-screen mode.
An internet connection is required for Spotify and album artwork.

For production:

- Terminate HTTPS at a reverse proxy or configure Kestrel HTTPS. Production uses
  secure cookies, HSTS, and HTTPS redirection.
- Register `https://YOUR_HOST/signin-spotify` in Spotify and set `AllowedHosts`
  to your host name. Deploy at the site root.
- Proxy WebSockets/SignalR and forward the original scheme. Forwarded headers
  use ASP.NET Core's default trusted loopback proxies; configure explicit
  `KnownIPProxies`/`KnownNetworks` in `Program.cs` if the proxy is elsewhere.
  Do not trust forwarded headers from arbitrary clients.
- Set credentials through your host's secret manager or environment, not user
  secrets (which are development-only).
- Set `DataDirectory` to a persistent, private directory writable by the server
  account. By default this is `App_Data` under the content root.
- Use a single application instance, or sticky sessions and an appropriately
  shared token/key directory. This implementation is intended for a small
  dedicated display, not a distributed multi-tenant service.

### Login persistence and security

The browser receives an HTTP-only authentication cookie containing a random
session identifier, **not Spotify credentials or tokens**. Login lasts up to
30 days, subject to Spotify revocation and cookie retention. Spotify OAuth uses
PKCE and the framework's protected state/correlation checks.

Access and refresh tokens are encrypted with ASP.NET Core Data Protection and
stored in `App_Data/tokens`. Keys are persisted in `App_Data/keys`; on Linux,
filesystem-persisted keys are not encrypted at rest by default. Restrict access
to the entire data directory and use encrypted storage or a supported external
key protector as appropriate. Keep both directories across restarts/deployments
to retain login; losing keys or token files requires signing in again.
Expired session files can be removed as part of routine maintenance. Logout
deletes that session's token file. `App_Data`, build output, and local environment
files are excluded from Git.

If Spotify was previously configured for the Vue app, rotate the old client
secret and clear the old `nowify_auth_state` browser storage: the previous app
placed credentials/tokens in the browser.

## Architecture

- `Program.cs`: authentication, OAuth callback, logout, hosting, and service registration.
- `Components/Pages/Home.razor`: login/idle/playing screens and cancellable polling.
- `Services/SpotifyPlayerService.cs`: Spotify HTTP calls, token renewal, response
  normalization, and error/rate-limit handling.
- `Services/TokenStore.cs`: protected persistent server-side token storage.
- `Models/PlaybackResult.cs`: minimal display model.
- `wwwroot/app.css`: responsive display styling.
- `wwwroot/nowify.js`: browser-side canvas colour extraction; no tokens or Spotify
  API requests. Artwork failures fall back to the default colours.

## Validation

```sh
dotnet build
dotnet publish -c Release
```

There is no automated test project. With configured Spotify credentials, verify
login, playback/track changes, pause/resume, idle playback, reload/restart
persistence, token renewal, and logout. Browser colour extraction needs artwork
that permits cross-origin canvas access; otherwise the default colours remain.

## Background

Original write-up:
[https://ashcroft.dev/blog/now-playing-screen-spotify-raspberry-pi-es6/](https://ashcroft.dev/blog/now-playing-screen-spotify-raspberry-pi-es6/)
