using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Nowify.Services;

public sealed record SpotifyTokens(
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset SessionExpiresAt);

public sealed class TokenStore
{
    private readonly IDataProtector protector;
    private readonly string directory;

    public TokenStore(IDataProtectionProvider protection, IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        protector = protection.CreateProtector("Nowify.SpotifyTokens.v1");
        directory = Path.Combine(configuration["DataDirectory"]
            ?? Path.Combine(environment.ContentRootPath, "App_Data"), "tokens");
        Directory.CreateDirectory(directory);
    }

    public async Task<SpotifyTokens?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = GetPath(sessionId);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var encrypted = await File.ReadAllTextAsync(path, cancellationToken);
            var tokens = JsonSerializer.Deserialize<SpotifyTokens>(protector.Unprotect(encrypted));
            if (tokens is not null && tokens.SessionExpiresAt > DateTimeOffset.UtcNow) return tokens;
            Delete(sessionId);
        }
        catch (Exception exception) when (exception is CryptographicException
            or JsonException or IOException)
        {
            return null;
        }
        return null;
    }

    public async Task SaveAsync(string sessionId, SpotifyTokens tokens,
        CancellationToken cancellationToken)
    {
        var path = GetPath(sessionId)
            ?? throw new ArgumentException("Invalid session identifier.", nameof(sessionId));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                protector.Protect(JsonSerializer.Serialize(tokens)), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Delete(string sessionId)
    {
        var path = GetPath(sessionId);
        if (path is not null) File.Delete(path);
    }

    private string? GetPath(string sessionId) =>
        Guid.TryParseExact(sessionId, "N", out _) ? Path.Combine(directory, sessionId + ".token") : null;
}
