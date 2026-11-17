using Altinn.App.Core.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// The test a shared host is currently serving. The fixture sets it before each test, and the host reads it
/// per request, so telemetry is tagged with the test that caused it although the host outlives every test.
/// </summary>
/// <remarks>
/// A plain field rather than an <see cref="AsyncLocal{T}"/> or xunit's <c>TestContext.Current</c>: the test
/// server does not flow the execution context from the client into the request pipeline, so a request would
/// not see the value the test set.
/// </remarks>
public sealed class CurrentTestId
{
    private TestId? _value;

    public TestId? Value
    {
        get => Volatile.Read(ref _value);
        set => Volatile.Write(ref _value, value);
    }
}

/// <summary>
/// Tags the activity and the metrics of every request with the current <see cref="TestId"/>, which the
/// <see cref="TelemetrySink"/> filters on.
/// </summary>
internal sealed class CurrentTestIdStartupFilter : IStartupFilter
{
    private readonly CurrentTestId _currentTestId;

    public CurrentTestIdStartupFilter(CurrentTestId currentTestId) => _currentTestId = currentTestId;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            builder.UseMiddleware<AddTestIdTagMiddleware>(_currentTestId);
            next(builder);
        };
    }

    private sealed class AddTestIdTagMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly CurrentTestId _currentTestId;

        public AddTestIdTagMiddleware(RequestDelegate next, CurrentTestId currentTestId)
        {
            _next = next;
            _currentTestId = currentTestId;
        }

        public Task Invoke(HttpContext httpContext)
        {
            if (_currentTestId.Value is { Value: var testId })
            {
                var activity = httpContext.Features.Get<IHttpActivityFeature>()?.Activity;
                activity?.AddTag(nameof(TestId), testId);
                var metrics = httpContext.Features.Get<IHttpMetricsTagsFeature>();
                metrics?.Tags.Add(new KeyValuePair<string, object?>(nameof(TestId), testId));
            }
            return _next(httpContext);
        }
    }
}
