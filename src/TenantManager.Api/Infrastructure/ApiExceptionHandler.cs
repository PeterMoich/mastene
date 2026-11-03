using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Application.Common;
using TenantManager.Domain.Common;

namespace TenantManager.Api.Infrastructure;

/// <summary>
/// Maps known exceptions to RFC 7807 ProblemDetails. Every response carries a machine-readable
/// "code" and the "traceId" (added in Program.cs). Anything not listed falls through to the
/// default handler, which logs it and returns a 500 without internal details.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails? details = exception switch
        {
            ValidationException v => new ValidationProblemDetails(v.Errors
                .GroupBy(e => ToCamelCase(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray()))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more fields are invalid.",
                Extensions = { ["code"] = "validation" },
            },
            NotFoundException n => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not found.",
                Detail = n.Message,
                Extensions = { ["code"] = "not_found" },
            },
            DomainRuleException d => new ProblemDetails
            {
                Status = d.Kind == RuleKind.Conflict ? StatusCodes.Status409Conflict
                    : StatusCodes.Status422UnprocessableEntity,
                Title = d.Message,
                Extensions = { ["code"] = d.Code },
            },
            UnauthorizedAccessException => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "You do not have access to this resource.",
                Extensions = { ["code"] = "forbidden" },
            },
            _ => null,
        };

        if (details is null) return false;

        httpContext.Response.StatusCode = details.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
