using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Blitztext.Core.AI;

public sealed class AIClientException(string message) : Exception(message)
{
    public static AIClientException NotConfigured() =>
        new("Kein Modell-Anbieter eingerichtet. Bitte in den Einstellungen einen API-Key hinterlegen.");

    public static AIClientException MissingKey(AIProvider provider) =>
        new($"Für {provider.DisplayName()} fehlt der API-Key.");

    public static AIClientException InvalidAddress() => new("Die Adresse des Modell-Anbieters ist ungültig.");

    public static AIClientException Refused() => new("Der Modell-Anbieter hat die Anfrage abgelehnt.");

    public static AIClientException EmptyResponse() =>
        new("Keine Antwort vom Modell-Anbieter erhalten. Bitte nochmal versuchen.");

    public static AIClientException Unreachable(AIProviderConfig config) => new(config.Provider == AIProvider.Custom
        ? $"Keine Verbindung zu {config.EffectiveBaseUrl}. Läuft der Server (z. B. Ollama oder LM Studio)?"
        : $"Keine Verbindung zu {config.Provider.DisplayName()}. Bitte die Internetverbindung prüfen.");

    public static AIClientException Http(int status, string? message) => status switch
    {
        401 or 403 => new("Der API-Key wurde abgelehnt. Bitte in den Einstellungen prüfen."),
        429 => new("Zu viele Anfragen oder das Guthaben beim Anbieter ist aufgebraucht."),
        _ => new($"Fehler vom Modell-Anbieter ({status})" + (message is null ? "." : $": {message}")),
    };
}

/// <summary>
/// One instruction plus one text in, one text out. Talks directly to the provider, so the
/// text never passes through a Blitztext server.
/// </summary>
public sealed class AIClient(IApiKeyStore keys, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string> CompleteAsync(string instruction, string text, AIProviderConfig config, CancellationToken cancellationToken = default)
    {
        var key = keys.Get(config.Provider);
        if (config.Provider.RequiresApiKey() && key is null)
        {
            throw AIClientException.MissingKey(config.Provider);
        }
        return config.Provider.ApiStyle() switch
        {
            AIApiStyle.Anthropic => await AnthropicAsync(instruction, text, config, key ?? "", cancellationToken),
            _ => await OpenAICompatibleAsync(instruction, text, config, key, cancellationToken),
        };
    }

    /// <summary>Sends a tiny request to check address, key and model.</summary>
    public Task TestConnectionAsync(AIProviderConfig config, CancellationToken cancellationToken = default) =>
        CompleteAsync("Antworte nur mit OK.", "Test", config, cancellationToken);

    // MARK: - OpenAI-compatible (OpenAI, Gemini, Mistral, OpenRouter, Ollama, LM Studio, ...)

    private async Task<string> OpenAICompatibleAsync(string instruction, string text, AIProviderConfig config, string? key, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(TrimmedBase(config.EffectiveBaseUrl) + "/chat/completions", UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https"))
        {
            throw AIClientException.InvalidAddress();
        }
        // No temperature: several current models only accept their default.
        var body = new JsonObject
        {
            ["model"] = config.EffectiveModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = instruction },
                new JsonObject { ["role"] = "user", ["content"] = text },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        if (config.Provider == AIProvider.OpenRouter)
        {
            request.Headers.TryAddWithoutValidation("X-Title", "Blitztext Windows");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (HttpRequestException)
        {
            throw AIClientException.Unreachable(config);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw AIClientException.Unreachable(config);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            JsonNode? root = null;
            try { root = JsonNode.Parse(json); } catch (JsonException) { }
            if (!response.IsSuccessStatusCode)
            {
                throw AIClientException.Http((int)response.StatusCode, ErrorMessage(root));
            }
            var content = root?["choices"]?[0]?["message"]?["content"];
            var result = content switch
            {
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                // Some servers send content parts.
                JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(result)) throw AIClientException.EmptyResponse();
            return result.Trim();
        }
    }

    // MARK: - Anthropic Messages API (official SDK)

    /// <summary>
    /// Models that accept <c>output_config.effort</c>; Haiku 4.5 and older models reject it with a 400.
    /// Short rewrites need little reasoning, so these run at low effort: fast and cheap.
    /// </summary>
    internal static bool SupportsEffort(string model) =>
        new[] { "claude-opus-5", "claude-fable", "claude-mythos", "claude-sonnet-5", "claude-haiku-5", "claude-opus-4-8",
                "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6" }
            .Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Models with safety classifiers that may decline a request. With <c>fallbacks: "default"</c>
    /// Anthropic re-runs a declined request on a suitable model server-side.
    /// </summary>
    internal static bool SupportsServerSideFallback(string model) =>
        model is "claude-opus-5-5" or "claude-opus-5" or "claude-sonnet-5-5" or "claude-fable-5" or "claude-fable-5-1";

    private static async Task<string> AnthropicAsync(string instruction, string text, AIProviderConfig config, string key, CancellationToken cancellationToken)
    {
        var model = config.EffectiveModel;
        var baseUrl = TrimmedBase(config.EffectiveBaseUrl);
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^3];
        var usesAnthropicApi = baseUrl.Equals(AIProvider.Anthropic.DefaultBaseUrl(), StringComparison.OrdinalIgnoreCase);

        var client = new AnthropicClient
        {
            ApiKey = key,
            BaseUrl = baseUrl,
        };

        var withFallback = usesAnthropicApi && SupportsServerSideFallback(model);
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16_000,
            System = instruction,
            Messages = [new BetaMessageParam { Role = Role.User, Content = text }],
            OutputConfig = SupportsEffort(model) ? new BetaOutputConfig { Effort = Effort.Low } : null,
            Fallbacks = withFallback ? new BetaFallbacksParam(new Default()) : null,
            Betas = withFallback ? [AnthropicBeta.ServerSideFallback2026_07_01] : null,
        };

        BetaMessage message;
        try
        {
            message = await client.Beta.Messages.Create(parameters, cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            throw AIClientException.Http((int)ex.StatusCode, ErrorMessage(ex.ResponseBody));
        }
        catch (HttpRequestException)
        {
            throw AIClientException.Unreachable(config);
        }

        if (message.StopReason is { } stopReason && stopReason.Raw() == "refusal")
        {
            throw AIClientException.Refused();
        }
        var result = string.Concat(message.Content
            .Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : ""))
            .Trim();
        if (result.Length == 0) throw AIClientException.EmptyResponse();
        return result;
    }

    // MARK: - Helpers

    /// <summary>Both API styles nest the message under "error".</summary>
    private static string? ErrorMessage(JsonNode? json)
    {
        var error = json?["error"];
        if (error is JsonObject obj) return obj["message"]?.GetValue<string>();
        if (error is JsonValue value && value.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static string? ErrorMessage(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try { return ErrorMessage(JsonNode.Parse(body)); }
        catch (JsonException) { return null; }
    }

    private static string TrimmedBase(string baseUrl) => baseUrl.Trim().TrimEnd('/', ' ');
}
