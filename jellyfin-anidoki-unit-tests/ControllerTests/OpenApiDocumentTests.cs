using System.Threading.Tasks;
using jellyfin_anidoki;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using NUnit.Framework;
using Swashbuckle.AspNetCore.Swagger;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

/// <summary>
/// Jellyfin builds one API document (<c>/api-docs/openapi.json</c>) from every controller it hosts, the plugin's
/// included, and names each schema after its type's short name. Two plugin types with the same short name in a
/// route's parameters or responses make that build throw, so the server's API docs answer 500.
/// </summary>
[TestFixture]
public class OpenApiDocumentTests {
    [Test]
    public async Task ThePluginsRoutesBuildAnApiDocument() {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager => {
            manager.ApplicationParts.Clear();
            manager.ApplicationParts.Add(new AssemblyPart(typeof(Plugin).Assembly));
        });
        builder.Services.AddSwaggerGen(options => options.SwaggerDoc("api-docs", new OpenApiInfo { Title = "Jellyfin API" }));
        await using var app = builder.Build();

        var document = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("api-docs");

        Assert.That(document.Paths, Is.Not.Empty);
    }
}
