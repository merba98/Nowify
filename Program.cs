using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Nowify.Components;
using Nowify.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.Configure<SpotifyOptions>(builder.Configuration.GetSection("Spotify"));
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<SpotifyPlayerService>();
builder.Services.AddHttpClient("Spotify", client =>
{
    client.BaseAddress = new Uri("https://api.spotify.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("SpotifyTokens", client =>
{
    client.BaseAddress = new Uri("https://accounts.spotify.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

var dataDirectory = builder.Configuration["DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("Nowify")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Nowify.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = false;
        options.LoginPath = "/auth/login";
    })
    .AddOAuth("Spotify", options =>
    {
        options.ClientId = builder.Configuration["Spotify:ClientId"] ?? "not-configured";
        options.ClientSecret = builder.Configuration["Spotify:ClientSecret"] ?? "not-configured";
        if (string.IsNullOrWhiteSpace(options.ClientId)) options.ClientId = "not-configured";
        if (string.IsNullOrWhiteSpace(options.ClientSecret)) options.ClientSecret = "not-configured";
        options.CallbackPath = "/signin-spotify";
        options.AuthorizationEndpoint = "https://accounts.spotify.com/authorize";
        options.TokenEndpoint = "https://accounts.spotify.com/api/token";
        options.Scope.Add("user-read-currently-playing");
        options.UsePkce = true;
        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                if (string.IsNullOrWhiteSpace(context.AccessToken)
                    || string.IsNullOrWhiteSpace(context.RefreshToken))
                    throw new InvalidOperationException("Spotify did not return the required tokens.");

                var sessionId = Guid.NewGuid().ToString("N");
                await context.HttpContext.RequestServices.GetRequiredService<TokenStore>().SaveAsync(
                    sessionId,
                    new SpotifyTokens(context.AccessToken, context.RefreshToken,
                        DateTimeOffset.UtcNow.Add(context.ExpiresIn ?? TimeSpan.FromHours(1)),
                        DateTimeOffset.UtcNow.AddDays(30)),
                    context.HttpContext.RequestAborted);
                context.Identity!.AddClaim(new Claim("nowify_session", sessionId));
                context.Identity.AddClaim(new Claim(ClaimTypes.Name, "Spotify listener"));
            },
            OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/?authError=true");
                return Task.CompletedTask;
            }
        };
    });

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/auth/login", (IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(configuration["Spotify:ClientId"])
        || string.IsNullOrWhiteSpace(configuration["Spotify:ClientSecret"]))
        return Results.Text("Configure Spotify:ClientId and Spotify:ClientSecret on the server first.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["Spotify"]);
});
app.MapPost("/auth/logout", async (HttpContext context, SpotifyPlayerService player,
    Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }
    var sessionId = context.User.FindFirstValue("nowify_session");
    if (sessionId is not null) await player.RevokeAsync(sessionId, context.RequestAborted);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/");
});
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
