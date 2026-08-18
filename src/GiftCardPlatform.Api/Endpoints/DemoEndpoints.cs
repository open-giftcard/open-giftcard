using System.Reflection;

namespace GiftCardPlatform.Api.Endpoints;

/// <summary>
/// Serves the development-only demonstration UI at <c>/demo</c>.
///
/// This is mapped only from the Development branch of the pipeline (see
/// <c>Program</c>), so outside Development the route does not exist and returns
/// 404. The page is a static asset that drives the public API through the same
/// JWT bearer and organization-context path as every other client. It holds no
/// business rules and has no privileged backend shortcut.
/// </summary>
internal static class DemoEndpoints
{
    private const string ResourceName = "GiftCardPlatform.Api.Demo.index.html";

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var html = LoadHtml();

        app.MapGet("/demo", () => Results.Content(html, "text/html; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static string LoadHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded demo resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
