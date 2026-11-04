using Chatbot.Api.Security;
using Chatbot.Application.Abstractions;

namespace Chatbot.Api.Controllers;

internal static class HttpContextExtensions
{
    public static ClientInfo Client(this HttpContext http) => new(http.Connection.RemoteIpAddress?.ToString());

    public static Guid ParticipantId(this HttpContext http) => http.User.ParticipantId();
}
