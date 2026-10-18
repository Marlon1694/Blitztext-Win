using System.Text.Json.Serialization;
using Blitztext.Core.Workflows;

namespace Blitztext.Core.AI;

/// <summary>A language model provider the user connects with their own API key.</summary>
[JsonConverter(typeof(TolerantEnumConverter<AIProvider>))]
public enum AIProvider
{
    OpenAI,
    Anthropic,
    Gemini,
    Mistral,
    OpenRouter,
    /// <summary>Any OpenAI-compatible endpoint: Azure, a company proxy, Ollama or LM Studio on this PC.</summary>
    Custom,
}

public enum AIApiStyle
{
    OpenAICompatible,
    Anthropic,
}

public static class AIProviders
{
    public static readonly AIProvider[] All =
        [AIProvider.OpenAI, AIProvider.Anthropic, AIProvider.Gemini, AIProvider.Mistral, AIProvider.OpenRouter, AIProvider.Custom];

    /// <summary>Stable name, used as the Credential Manager account.</summary>
    public static string Key(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "openAI",
        AIProvider.Anthropic => "anthropic",
        AIProvider.Gemini => "gemini",
        AIProvider.Mistral => "mistral",
        AIProvider.OpenRouter => "openRouter",
        AIProvider.Custom => "custom",
        _ => "custom",
    };

    public static string DisplayName(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "OpenAI",
        AIProvider.Anthropic => "Anthropic (Claude)",
        AIProvider.Gemini => "Google Gemini",
        AIProvider.Mistral => "Mistral",
        AIProvider.OpenRouter => "OpenRouter",
        AIProvider.Custom => "Eigene Adresse (Ollama, LM Studio …)",
        _ => "",
    };

    public static AIApiStyle ApiStyle(this AIProvider provider) =>
        provider == AIProvider.Anthropic ? AIApiStyle.Anthropic : AIApiStyle.OpenAICompatible;

    /// <summary>Base URL; for OpenAI-compatible providers <c>/chat/completions</c> is appended.</summary>
    public static string DefaultBaseUrl(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "https://api.openai.com/v1",
        AIProvider.Anthropic => "https://api.anthropic.com",
        AIProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        AIProvider.Mistral => "https://api.mistral.ai/v1",
        AIProvider.OpenRouter => "https://openrouter.ai/api/v1",
        AIProvider.Custom => "http://localhost:11434/v1",
        _ => "",
    };

    /// <summary>Suggested model, editable in the settings.</summary>
    public static string DefaultModel(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "gpt-4o-mini",
        AIProvider.Anthropic => "claude-opus-5-5",
        AIProvider.Gemini => "gemini-2.5-flash",
        AIProvider.Mistral => "mistral-small-latest",
        AIProvider.OpenRouter => "openai/gpt-4o-mini",
        AIProvider.Custom => "llama3.2",
        _ => "",
    };

    /// <summary>Local servers like Ollama run without a key.</summary>
    public static bool RequiresApiKey(this AIProvider provider) => provider != AIProvider.Custom;

    public static string? KeyHelpUrl(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "https://platform.openai.com/api-keys",
        AIProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        AIProvider.Gemini => "https://aistudio.google.com/apikey",
        AIProvider.Mistral => "https://console.mistral.ai/api-keys",
        AIProvider.OpenRouter => "https://openrouter.ai/keys",
        _ => null,
    };
}

/// <summary>Which provider is connected, with which model and address. The key lives in the Credential Manager.</summary>
public sealed record AIProviderConfig
{
    public AIProvider Provider { get; init; }
    public string Model { get; init; } = "";
    public string BaseUrl { get; init; } = "";

    public AIProviderConfig() { }

    public AIProviderConfig(AIProvider provider, string? model = null, string? baseUrl = null)
    {
        Provider = provider;
        Model = model ?? provider.DefaultModel();
        BaseUrl = baseUrl ?? provider.DefaultBaseUrl();
    }

    public string EffectiveModel => string.IsNullOrWhiteSpace(Model) ? Provider.DefaultModel() : Model.Trim();
    public string EffectiveBaseUrl => string.IsNullOrWhiteSpace(BaseUrl) ? Provider.DefaultBaseUrl() : BaseUrl.Trim();
}

/// <summary>API keys per provider, stored by the platform (Windows Credential Manager).</summary>
public interface IApiKeyStore
{
    string? Get(AIProvider provider);
    bool Save(AIProvider provider, string key);
    void Delete(AIProvider provider);
}

public static class ApiKeyStoreExtensions
{
    /// <summary>"sk-…abcd" for display, never the full key.</summary>
    public static string? MaskedKey(this IApiKeyStore store, AIProvider provider)
    {
        var key = store.Get(provider);
        if (key is null) return null;
        if (key.Length <= 8) return "••••••••";
        return key[..3] + "…" + key[^4..];
    }

    /// <summary>True when a provider is set up and has its key (or needs none).</summary>
    public static bool IsConfigured(this IApiKeyStore store, AIProviderConfig? config) =>
        config is not null && (!config.Provider.RequiresApiKey() || store.Get(config.Provider) is not null);
}

/// <summary>Keeps keys in memory; for tests and for runs without a credential store.</summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<AIProvider, string> _keys = [];

    public string? Get(AIProvider provider) => _keys.TryGetValue(provider, out var key) ? key : null;

    public bool Save(AIProvider provider, string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) _keys.Remove(provider);
        else _keys[provider] = trimmed;
        return true;
    }

    public void Delete(AIProvider provider) => _keys.Remove(provider);
}
