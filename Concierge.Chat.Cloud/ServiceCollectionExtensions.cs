using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Chat.Cloud;

public static class ConciergeCloudChatServiceCollectionExtensions
{
    /// <summary>Provider id constants used for keyed DI lookup and conversation persistence.</summary>
    public static class ProviderIds
    {
        public const string OpenAi = "openai";
        public const string Anthropic = "anthropic";
        public const string Gemini = "gemini";
    }

    /// <summary>
    /// Registers the OpenAI runtime under the keyed id <c>"openai"</c> and adds it to the
    /// <c>IEnumerable&lt;IChatRuntime&gt;</c> set the chat UI walks to populate its provider
    /// selector. The host owns the <see cref="OpenAiChatOptions"/> factory so the key never
    /// lives in source code — usually it comes from <c>IConfiguration</c>.
    /// </summary>
    public static IServiceCollection AddOpenAiChat(
        this IServiceCollection services,
        Func<IServiceProvider, OpenAiChatOptions> optionsFactory)
    {
        services.AddHttpClient<OpenAiChatRuntime>((sp, client) =>
        {
            var options = optionsFactory(sp);
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton(optionsFactory);
        services.AddKeyedSingleton<IChatRuntime>(ProviderIds.OpenAi,
            (sp, _) => sp.GetRequiredService<OpenAiChatRuntime>());
        services.AddSingleton<IChatRuntime>(sp => sp.GetRequiredService<OpenAiChatRuntime>());
        return services;
    }

    public static IServiceCollection AddAnthropicChat(
        this IServiceCollection services,
        Func<IServiceProvider, AnthropicChatOptions> optionsFactory)
    {
        services.AddHttpClient<AnthropicChatRuntime>((sp, client) =>
        {
            var options = optionsFactory(sp);
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton(optionsFactory);
        services.AddKeyedSingleton<IChatRuntime>(ProviderIds.Anthropic,
            (sp, _) => sp.GetRequiredService<AnthropicChatRuntime>());
        services.AddSingleton<IChatRuntime>(sp => sp.GetRequiredService<AnthropicChatRuntime>());
        return services;
    }

    public static IServiceCollection AddGeminiChat(
        this IServiceCollection services,
        Func<IServiceProvider, GeminiChatOptions> optionsFactory)
    {
        services.AddHttpClient<GeminiChatRuntime>((sp, client) =>
        {
            var options = optionsFactory(sp);
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton(optionsFactory);
        services.AddKeyedSingleton<IChatRuntime>(ProviderIds.Gemini,
            (sp, _) => sp.GetRequiredService<GeminiChatRuntime>());
        services.AddSingleton<IChatRuntime>(sp => sp.GetRequiredService<GeminiChatRuntime>());
        return services;
    }
}
