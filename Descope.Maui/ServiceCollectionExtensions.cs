using Microsoft.Extensions.DependencyInjection;

namespace Descope.Maui;

/// <summary>
/// Registers Descope with a MAUI app's services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="DescopeClient"/> and its <see cref="DescopeSessionManager"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddDescope(options =>
    /// {
    ///     options.ProjectId = "P...";
    ///     options.RedirectUri = "myapp://auth";
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddDescope(this IServiceCollection services, Action<DescopeOptions> configure)
    {
        var options = new DescopeOptions();
        configure(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(_ => new DescopeClient(options));
        services.AddSingleton(sp => sp.GetRequiredService<DescopeClient>().SessionManager);
        return services;
    }
}
