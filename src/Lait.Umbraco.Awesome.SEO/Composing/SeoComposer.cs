using Lait.Umbraco.Awesome.SEO.Ai;
using Lait.Umbraco.Awesome.SEO.Configuration;
using Lait.Umbraco.Awesome.SEO.Persistence;
using Lait.Umbraco.Awesome.SEO.Search;
using Lait.Umbraco.Awesome.SEO.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.Cms.Api.Management.OpenApi;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Lait.Umbraco.Awesome.SEO.Composing;

public static class SeoApi
{
    public const string Name = "lait-seo";
}

public sealed class SeoComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<SeoOptions>().Bind(builder.Config.GetSection(SeoOptions.SectionName));

        // Redirects are followed by the crawler itself so chains and loops can be reported.
        // MVC does not discover controllers in this class library on its own (not in the app's dependency context), so register it.
        builder.Services.AddControllers().AddApplicationPart(typeof(SeoComposer).Assembly);

        builder.Services.AddHttpClient(SeoRunner.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });

        // Own Swagger document so the API is grouped and gets Umbraco's backoffice auth in Swagger UI.
        builder.Services.Configure<SwaggerGenOptions>(o =>
        {
            o.SwaggerDoc(SeoApi.Name, new OpenApiInfo { Title = "Lait SEO Report API", Version = "1.0" });
            o.OperationFilter<SeoOperationSecurityFilter>();
        });

        // The AI options are appsettings + whatever is saved in the Indstillinger tab, so AiSettings *is* the options monitor.
        builder.Services.AddSingleton<AiSettings>();
        builder.Services.AddSingleton<IOptionsMonitor<AiOptions>>(sp => sp.GetRequiredService<AiSettings>());
        builder.Services.AddSingleton<ConnectionTester>();
        builder.Services.AddHttpClient(JevTriageClient.HttpClientName);
        builder.Services.AddSingleton<IChatClientFactory, AnthropicChatClientFactory>();
        builder.Services.AddSingleton<AiPipeline>();
        builder.Services.AddSingleton<JevImpactScorer>();
        builder.Services.AddSingleton<PlanWriter>();
        builder.Services.AddSingleton<PlanService>();

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(SearchConsoleClient.HttpClientName);
        builder.Services.AddSingleton<SearchConsoleSettings>();
        builder.Services.AddSingleton<SearchConsoleClient>();

        builder.Services.AddSingleton<SeoStore>();
        builder.Services.AddSingleton<SiteSettings>();
        builder.Services.AddSingleton<ContentLookup>();
        builder.Services.AddSingleton<SeoRunner>();
        builder.Services.AddHostedService<SeoScheduler>();
    }
}

public sealed class SeoOperationSecurityFilter : BackOfficeSecurityRequirementsOperationFilterBase
{
    protected override string ApiName => SeoApi.Name;
}
