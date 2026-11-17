using Altinn.App.Api.Extensions;
using Altinn.App.Api.Helpers;
using Altinn.App.Api.Tests.Data;
using Altinn.App.Api.Tests.Fixtures;
using Altinn.App.Api.Tests.Fixtures.Platform;
using Altinn.App.Api.Tests.Mocks;
using Altinn.App.Api.Tests.Mocks.Authentication;
using Altinn.App.Api.Tests.Mocks.Event;
using Altinn.App.Core.Configuration;
using Altinn.App.Core.Features;
using Altinn.App.Core.Infrastructure.Clients.Register;
using Altinn.App.Core.Internal.App;
using Altinn.App.Core.Internal.AppModel;
using Altinn.App.Core.Internal.Auth;
using Altinn.App.Core.Internal.Events;
using Altinn.App.Core.Internal.Instances;
using Altinn.App.Core.Internal.Profile;
using Altinn.App.Core.Internal.Registers;
using Altinn.App.Core.Internal.Sign;
using Altinn.App.Tests.Common;
using Altinn.App.Tests.Common.Mocks;
using AltinnCore.Authentication.JwtCookie;
using App.IntegrationTests.Mocks.Services;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

// An xunit v3 test project is an executable. The test runner starts this program to run the tests, and
// WebApplicationFactory invokes the same entry point again, on a thread of its own, to build each test host.
if (!AppApiFixture.IsWebApplicationFactoryCall(args))
{
    return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);
}

// From here on this file should be as close to the Program.cs file in the app template
// as possible to ensure we test the configuration of the dependency injection
// container in this project.
// External interfaces like Platform related services, Authentication, Authorization
// external api's etc. should be mocked.

// Use the test app as the default content root. Startup validation needs its process and application
// metadata; the build output contains only stub metadata.
string? contentRootFromArgs = new ConfigurationBuilder().AddCommandLine(args).Build()[WebHostDefaults.ContentRootKey];

WebApplicationBuilder builder = WebApplication.CreateBuilder(
    new WebApplicationOptions()
    {
        ContentRootPath = Directory.Exists(contentRootFromArgs)
            ? contentRootFromArgs
            : TestData.GetApplicationDirectory("tdd", "contributer-restriction"),
        ApplicationName = "Altinn.App.Api.WebApplicationFactory.Tests",
        WebRootPath = Path.Join(TestData.GetTestDataRootDirectory(), "apps", "tdd", "contributer-restriction"),
        EnvironmentName = "Production",
    }
);

builder.WebHost.UseDefaultServiceProvider(
    (context, options) =>
    {
        options.ValidateScopes = true; // Always validate scopes in test
        options.ValidateOnBuild = true;
    }
);

AppApiFixture.ConfigureFakeLogging(builder.Logging);

// The host is shared by the tests of a class, so the current test is a value the fixture changes between
// tests rather than a singleton fixed when the host is built. The fixture replaces this registration with
// the holder it owns.
builder.Services.TryAddSingleton<CurrentTestId>();
builder.Services.AddSingleton<IStartupFilter, CurrentTestIdStartupFilter>();

builder.Configuration.AddJsonFile(
    Path.Join(TestData.GetTestDataRootDirectory(), "apps", "tdd", "contributer-restriction", "appsettings.json")
);
builder.Configuration.GetSection("MetricsSettings:Enabled").Value = "false";
builder.Configuration.GetSection("AppSettings:UseOpenTelemetry").Value = "true";
builder.Services.Configure<ApplicationInsightsServiceOptions>(options =>
    options.RequestCollectionOptions.InjectResponseHeaders = false
);
builder.Services.Configure<GeneralSettings>(settings => settings.DisableLocaltestValidation = true);
builder.Services.Configure<GeneralSettings>(settings => settings.IsTest = true);
builder.Configuration.GetSection("GeneralSettings:IsTest").Value = "true";

// The platform tells an app where it provisioned its secrets and what it called each file, and it provisions
// both files the libraries host: the app's one Maskinporten client, and the callback verification codes whose
// WorkflowEngineCallback entry every test host needs to pass the always-on startup validation. The libraries
// require all of it and refuse to start without it, so stand in for the platform with a throwaway directory.
foreach ((string key, string? value) in ProvisionedSecretsTestEnvironment.Variables)
{
    builder.Configuration[key] = value;
}

ConfigureServices(builder.Services, builder.Configuration);
ConfigurePlatformServices(builder.Services, TestPlatformSelection.Platform);

WebApplication app = builder.Build();
Configure();
app.Run();
return 0;

void ConfigureServices(IServiceCollection services, IConfiguration config)
{
    services.AddAltinnAppControllersWithViews();
    services.AddAltinnAppServices(config, builder.Environment);
    // Add Swagger support (Swashbuckle)
    services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Altinn App Api", Version = "v1" });
        StartupHelper.IncludeXmlComments(c.IncludeXmlComments);
    });
}

// The platform the app talks to: in-memory mocks, or (once implemented) a real localtest that the
// unchanged clients from Altinn.App.Core reach through the platform settings.
void ConfigurePlatformServices(IServiceCollection services, IAppTestPlatform platform)
{
    services.AddSingleton<IOptions<PlatformSettings>>(Options.Create(platform.PlatformSettings));

    switch (platform.Kind)
    {
        case TestPlatform.InMemory:
            ConfigureMockServices(services);
            break;
        case TestPlatform.Localtest:
            throw new NotSupportedException(
                $"{nameof(TestPlatform.Localtest)} is not implemented: keep the platform clients from "
                    + "AddAltinnAppServices and point them at localtest here."
            );
        default:
            throw new ArgumentOutOfRangeException(nameof(platform), platform.Kind, "Unknown test platform");
    }

    services.PostConfigureAll<JwtCookieOptions>(options =>
    {
        // During tests we generate tokens immediately before trying to validate them.
        // Depending on the clock implementation used from the current OS, the clock may not be
        // monotonically increasing, so there is a non-zero chance we experience issues with 'nbf' for example
        // So since this is only relevant during tests we just amp up the clock skew to be safe
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(10);

        // Failed token validation during tests should output logs
        options.Events = new JwtCookieEvents
        {
            OnAuthenticationFailed = (context) =>
            {
                var services = context.HttpContext.RequestServices;
                var logger = services.GetRequiredService<ILogger<JwtCookieOptions>>();
                logger.LogError(context.Exception, "Authentication failed");
                return Task.CompletedTask;
            },
            OnTokenValidated = (context) =>
            {
                return Task.CompletedTask;
            },
        };
    });
}

// Every registration here replaces a client of the platform with an in-memory stand-in. Storage is the
// exception: the app's own InstanceClient and DataClient stay and talk to an in-memory fake of the Storage API.
void ConfigureMockServices(IServiceCollection services)
{
    InMemoryStorage.Add(services);
    services.AddTransient<IAuthorizationClient, AuthorizationMock>();
    services.AddSingleton<Altinn.Common.PEP.Interfaces.IPDP, PepWithPDPAuthorizationMockSI>();
    services.AddSingleton<IPostConfigureOptions<JwtCookieOptions>, JwtCookiePostConfigureOptionsStub>();
    services.AddTransient<AltinnPartyClientInterceptor>();
    services
        .AddHttpClient<IAltinnPartyClient, AltinnPartyClient>()
        .ConfigurePrimaryHttpMessageHandler<AltinnPartyClientInterceptor>();
    services.AddTransient<IRegisterClient, RegisterClientMock>();
    services.AddTransient<IProfileClient, ProfileClientMock>();
    services.AddTransient<IInstanceEventClient, InstanceEventClientMock>();
    services.AddTransient<IAppModel, AppModelMock<Program>>();
    services.AddTransient<IEventsClient, EventsClientMock>();
    services.AddTransient<ISignClient, SignClientMock>();
}

void Configure()
{
    app.UseSwagger(o => o.RouteTemplate = "/swagger/{documentName}/swagger.{json|yaml}");

    // Enable middleware to serve generated Swagger as a JSON endpoint.
    // This is used for testing, and don't use the appId prefix used in real apps
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint($"/swagger/v1/swagger.json", "Altinn App API");
        c.RoutePrefix = "/swagger";
    });
    app.UseAltinnAppCommonConfiguration();
}

// This "hack" (documented by Microsoft) is done to
// make the Program class public and available for
// integration tests.
public partial class Program { }
