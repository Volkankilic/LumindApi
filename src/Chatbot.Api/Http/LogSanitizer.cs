using Serilog.Core;
using Serilog.Events;

namespace Chatbot.Api.Http;

/// <summary>
/// ASP.NET Core wraps every request in a log scope that carries the RAW request path (with ids), which Serilog copies onto
/// every event logged during the request. Our logs hold the route TEMPLATE only, so these properties are removed
/// before an event reaches any sink.
/// </summary>
public sealed class SensitiveScopeEnricher : ILogEventEnricher
{
    private static readonly string[] Properties = ["RequestPath", "Path", "QueryString", "Query", "RawUrl"];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var name in Properties)
        {
            logEvent.RemovePropertyIfPresent(name);
        }
    }
}
