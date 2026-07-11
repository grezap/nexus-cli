using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// Spectre.Console.Cli &lt;-&gt; Microsoft.Extensions.DependencyInjection bridge.
/// Pattern from spectreconsole.net/cli/registrars; AOT-safe under bounded
/// reflection (Spectre activates registered command types via their public
/// constructors).
/// </summary>
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    /// <summary>Creates the registrar over the MS.DI service collection Spectre populates during configuration.</summary>
    /// <param name="services">The collection commands + settings are registered into.</param>
    public TypeRegistrar(IServiceCollection services) => _services = services;

    /// <inheritdoc />
    public ITypeResolver Build() => new TypeResolver(_services.BuildServiceProvider());

    /// <inheritdoc />
    // Singleton lifetime: Spectre resolves each command type once per invocation of a short-lived CLI process.
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Spectre passes command types whose public constructors are preserved by DI registration.")]
    public void Register(Type service, Type implementation)
        => _services.AddSingleton(service, implementation);

    /// <inheritdoc />
    public void RegisterInstance(Type service, object implementation)
        => _services.AddSingleton(service, implementation);

    /// <inheritdoc />
    // Factory deferral: wrap Spectre's Func in a singleton factory so the instance is built lazily on first resolve.
    public void RegisterLazy(Type service, Func<object> factory)
        => _services.AddSingleton(service, _ => factory());
}

/// <summary>
/// Resolver half of the Spectre &lt;-&gt; MS.DI bridge: adapts a built <see cref="ServiceProvider"/>
/// to Spectre's <see cref="ITypeResolver"/> and disposes it when the app exits.
/// </summary>
internal sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly ServiceProvider _provider;

    /// <summary>Creates the resolver over an already-built service provider.</summary>
    /// <param name="provider">The provider Spectre resolves command instances from.</param>
    public TypeResolver(ServiceProvider provider) => _provider = provider;

    /// <inheritdoc />
    public object? Resolve(Type? type) => type is null ? null : _provider.GetService(type);

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}
