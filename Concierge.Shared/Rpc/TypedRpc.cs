using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Concierge.Shared.Rpc;

/// <summary>Names the wire method a typed member stands for.</summary>
/// <remarks>
/// Without it the method name is used, so <c>NewSession</c> would be sent as
/// <c>newSession</c> rather than <c>session/new</c>. Protocol names are not C# names and
/// should not be forced to be.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcMethodAttribute(string method) : Attribute
{
    /// <summary>The method name as it goes on the wire.</summary>
    public string Method { get; } = method;
}

/// <summary>
/// Turns a plain C# interface into a JSON-RPC client, so callers reach the runtime through
/// types rather than by spelling method names into dictionaries.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="DispatchProxy"/>, which is in the base class library — so a typed
/// surface costs no code generator, no build step, and no dependency.
/// </para>
/// <para>
/// Every method must return <c>Task&lt;T&gt;</c>: a synchronous call over a pipe would block
/// whatever thread asked, and on a phone that is the one drawing the screen.
/// </para>
/// </remarks>
public class TypedRpcProxy : DispatchProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private JsonRpcClient _client = null!;

    /// <summary>Build a typed client over an established connection.</summary>
    public static T Create<T>(JsonRpcClient client) where T : class
    {
        ArgumentNullException.ThrowIfNull(client);

        var proxy = Create<T, TypedRpcProxy>() as TypedRpcProxy
            ?? throw new InvalidOperationException("The proxy could not be created.");
        proxy._client = client;

        return (proxy as T)!;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new InvalidOperationException("A typed call arrived with no method.");
        }

        var method = targetMethod.GetCustomAttribute<RpcMethodAttribute>()?.Method ?? targetMethod.Name;
        var parameters = BuildParameters(targetMethod, args);

        var returnType = targetMethod.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            throw new InvalidOperationException(
                $"'{targetMethod.Name}' must return Task<T>: a blocking call over a pipe would stall its caller.");
        }

        var resultType = returnType.GetGenericArguments()[0];

        // Reflection is needed because the result type is only known at run time.
        var invoker = typeof(TypedRpcProxy)
            .GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(resultType);

        return invoker.Invoke(this, [method, parameters]);
    }

    private async Task<TResult> InvokeAsync<TResult>(string method, JsonObject? parameters)
    {
        var result = await _client.InvokeAsync(method, parameters).ConfigureAwait(false);
        if (result is null)
        {
            return default!;
        }

        return result.Deserialize<TResult>(JsonOptions)!;
    }

    /// <summary>
    /// Names the arguments after the parameters they were passed as, so the wire object reads
    /// the way the interface does. Nulls are left out rather than sent as null.
    /// </summary>
    private static JsonObject? BuildParameters(MethodInfo method, object?[]? args)
    {
        var declared = method.GetParameters();
        if (declared.Length == 0 || args is null)
        {
            return null;
        }

        var parameters = new JsonObject();
        for (var index = 0; index < declared.Length && index < args.Length; index++)
        {
            if (args[index] is null)
            {
                continue;
            }

            parameters[declared[index].Name!] = JsonSerializer.SerializeToNode(args[index], JsonOptions);
        }

        return parameters;
    }
}
