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

    public TypeRegistrar(IServiceCollection services) => _services = services;

    public ITypeResolver Build() => new TypeResolver(_services.BuildServiceProvider());

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Spectre passes command types whose public constructors are preserved by DI registration.")]
    public void Register(Type service, Type implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory)
        => _services.AddSingleton(service, _ => factory());
}

internal sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly ServiceProvider _provider;

    public TypeResolver(ServiceProvider provider) => _provider = provider;

    public object? Resolve(Type? type) => type is null ? null : _provider.GetService(type);

    public void Dispose() => _provider.Dispose();
}
