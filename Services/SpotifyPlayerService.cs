using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nowify.Models;

namespace Nowify.Services;

public sealed class SpotifyPlayerService(
    IHttpClientFactory clients, TokenStore store, IOptions<SpotifyOptions> options)
{
    // Striped locks serialize refreshes for a session without retaining a lock per visitor.
    private readonly SemaphoreSlim[] gates =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task RevokeAsync(string sessionId, CancellationToken cancellationToken)
    {
        var gate = gates[(uint)StringComparer.Ordinal.GetHashCode(sessionId) % (uint)gates.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            store.Delete(sessionId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlaybackResult> GetNowPlayingAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        var gate = gates[(uint)StringComparer.Ordinal.GetHashCode(sessionId) % (uint)gates.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var tokens = await store.GetAsync(sessionId, cancellationToken);
            if (tokens is null) return new(RequiresLogin: true);

            if (tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refresh = await RefreshAsync(tokens, sessionId, cancellationToken);
                if (refresh.Error is not null) return refresh.Error;
                tokens = refresh.Tokens!;
            }

            using var first = await FetchAsync(tokens.AccessToken, cancellationToken);
            if (first.StatusCode != HttpStatusCode.Unauthorized)
                return await ReadPlaybackAsync(first, cancellationToken);

            var retry = await RefreshAsync(tokens, sessionId, cancellationToken);
            if (retry.Error is not null) return retry.Error;
            using var second = await FetchAsync(retry.Tokens!.AccessToken, cancellationToken);
            if (second.StatusCode == HttpStatusCode.Unauthorized)
                return new(RequiresLogin: true);
            return await ReadPlaybackAsync(second, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(Error: "Spotify took too long to respond. Retrying shortly.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return new(Error: "Unable to reach Spotify. Retrying shortly.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HttpResponseMessage> FetchAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "me/player/currently-playing");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await clients.CreateClient("Spotify").SendAsync(request, cancellationToken);
    }

    private async Task<(SpotifyTokens? Tokens, PlaybackResult? Error)> RefreshAsync(
        SpotifyTokens tokens, string sessionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{options.Value.ClientId}:{options.Value.ClientSecret}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken
        });
        using var response = await clients.CreateClient("SpotifyTokens").SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            store.Delete(sessionId);
            return (null, new(RequiresLogin: true));
        }
        if (!response.IsSuccessStatusCode)
            return (null, Failure(response));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var accessToken = String(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
            return (null, new(Error: "Spotify could not renew the session. Retrying shortly."));
        var lifetime = root.TryGetProperty("expires_in", out var expires)
            && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 1, 86400) : 3600;
        var updated = tokens with
        {
            AccessToken = accessToken,
            RefreshToken = String(root, "refresh_token") ?? tokens.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime)
        };
        await store.SaveAsync(sessionId, updated, cancellationToken);
        return (updated, null);
    }

    private static async Task<PlaybackResult> ReadPlaybackAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return new();
        if (!response.IsSuccessStatusCode) return Failure(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("is_playing", out var playing) || playing.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return new();

        var artists = Array(item, "artists")
            .Select(artist => String(artist, "name")).OfType<string>().ToArray();
        var albumTitle = "";
        string? image = null;
        if (item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumTitle = String(album, "name") ?? "";
            image = Array(album, "images").Select(value => String(value, "url")).FirstOrDefault();
        }
        else if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)
        {
            albumTitle = String(show, "name") ?? "";
            artists = [String(show, "publisher") ?? albumTitle];
            image = Array(item, "images").Select(value => String(value, "url")).FirstOrDefault();
        }
        if (!Uri.TryCreate(image, UriKind.Absolute, out var imageUri) || imageUri.Scheme != "https")
            image = null;
        var title = String(item, "name") ?? "Unknown track";
        return new(new NowPlayingTrack(String(item, "id") ?? String(item, "uri") ?? title,
            title, artists, albumTitle, image));
    }

    private static PlaybackResult Failure(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                ?? TimeSpan.FromSeconds(30);
            return new(Error: "Spotify is rate limiting requests. Waiting before retrying.",
                RetryAfter: delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1));
        }
        return new(Error: "Spotify is temporarily unavailable. Retrying shortly.");
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
