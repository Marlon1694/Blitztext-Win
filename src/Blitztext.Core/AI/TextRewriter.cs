namespace Blitztext.Core.AI;

/// <summary>
/// Rewrites a transcript with an instruction through the provider the user connected:
/// a cloud provider with their own API key or a local server such as Ollama or LM Studio.
/// </summary>
public sealed class TextRewriter(AIClient client, IApiKeyStore keys, Func<AIProviderConfig?> config)
{
    public static string NoModelMessage =>
        "Für diesen Modus braucht Blitztext einen Modell-Anbieter: einen API-Key oder einen lokalen Server wie Ollama.";

    /// <summary>Whether the AI step can run.</summary>
    public bool CanRewrite => keys.IsConfigured(config());

    public async Task<string> RewriteAsync(string text, string instruction, CancellationToken cancellationToken = default)
    {
        var current = config();
        if (current is null || !keys.IsConfigured(current))
        {
            throw new AIClientException(NoModelMessage);
        }
        return await client.CompleteAsync(
            instruction + "\n\n" + ModePrompts.SpokenTranscriptRules,
            text,
            current,
            cancellationToken);
    }
}
