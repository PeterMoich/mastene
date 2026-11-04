using FluentValidation;
using TenantManager.Application.Common;
using TenantManager.Domain.Issues;

namespace TenantManager.Application.Issues;

/// <param name="LeaseId">Required for a tenant: the lease the issue is about.</param>
/// <param name="UnitId">For the landlord: the unit. Linked to the unit's current lease and tenant if it is let.</param>
public sealed record CreateIssueRequest(
    Guid? LeaseId, Guid? UnitId, IssueCategory Category, IssuePriority Priority, string Title, string Description);

public sealed record ResolveIssueRequest(string? Note);

public sealed record ReopenIssueRequest(string Reason);

/// <param name="ContractorId">Null removes the contractor.</param>
public sealed record AssignContractorRequest(Guid? ContractorId);

public sealed record ChangePriorityRequest(IssuePriority Priority);

public sealed record IssueCommentRequest(string Body);

public sealed record IssueListQuery : PagedQuery
{
    public IssueStatus? Status { get; init; }
    public IssuePriority? Priority { get; init; }
    public Guid? PropertyId { get; init; }
    public Guid? LeaseId { get; init; }
    public Guid? ContractorId { get; init; }

    /// <summary>Only issues that missed a target (true) or that didn't (false).</summary>
    public bool? Escalated { get; init; }

    /// <summary>True: everything not closed yet.</summary>
    public bool? OpenOnly { get; init; }
}

public sealed record IssueSummaryDto(
    Guid Id, string Title, IssueCategory Category, IssuePriority Priority, IssueStatus Status, Guid UnitId,
    string PropertyName, string UnitLabel, string? TenantName, string? ContractorName, DateTimeOffset OpenedAt,
    DateTimeOffset ResponseDueAt, DateTimeOffset ResolutionDueAt, bool IsEscalated, int PhotoCount);

public sealed record ContractorRefDto(Guid Id, string Name, string Trade, string Phone);

public sealed record IssuePhotoDto(Guid Id, string FileName, string ContentType, long SizeBytes, string UploadedByName,
    DateTimeOffset At);

public sealed record IssueCommentDto(Guid Id, string AuthorName, bool ByTenant, string Body, DateTimeOffset At);

public sealed record IssueEventDto(IssueEventKind Kind, string ByName, DateTimeOffset At, string? Note);

/// <param name="ResponseMet">True if acknowledged on time, false if late (or still waiting past the target), null while waiting.</param>
/// <param name="AllowedActions">
/// For the current user: acknowledge, start, resolve, close, reopen, assignContractor, changePriority, comment, addPhoto.
/// </param>
public sealed record IssueDto(
    Guid Id, string Title, string Description, IssueCategory Category, IssuePriority Priority, IssueStatus Status,
    Guid UnitId, string PropertyName, string UnitLabel, Guid? LeaseId, string? LeaseNumber, Guid? TenantId,
    string? TenantName, string ReportedByName, ContractorRefDto? Contractor,
    DateTimeOffset OpenedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? StartedAt, DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt, DateTimeOffset ResponseDueAt, DateTimeOffset ResolutionDueAt, bool? ResponseMet,
    bool? ResolutionMet, bool IsEscalated, DateTimeOffset? ResponseMissedAt, DateTimeOffset? ResolutionMissedAt,
    string? ResolutionNote, int ReopenCount, int AutoCloseDays, IReadOnlyList<IssuePhotoDto> Photos,
    IReadOnlyList<IssueCommentDto> Comments, IReadOnlyList<IssueEventDto> Events, IReadOnlyList<string> AllowedActions);

/// <summary>How one priority did against its targets, for issues opened in the period.</summary>
public sealed record SlaPriorityDto(
    IssuePriority Priority, int ResponseTargetHours, int ResolutionTargetHours, int Opened, int Acknowledged,
    int AcknowledgedOnTime, int Resolved, int ResolvedOnTime, double? AverageResponseHours,
    double? AverageResolutionHours, int Escalated, int StillOpen);

public sealed record SlaReportDto(DateOnly From, DateOnly To, IReadOnlyList<SlaPriorityDto> Priorities);

/// <summary>What one run of the SLA checks changed.</summary>
public sealed record IssueChecksSummary(int Escalated, int AutoClosed);

public sealed class CreateIssueRequestValidator : AbstractValidator<CreateIssueRequest>
{
    public CreateIssueRequestValidator()
    {
        RuleFor(x => x).Must(x => x.LeaseId is not null || x.UnitId is not null)
            .WithName("unitId").WithMessage("Choose the unit or lease the issue is about.");
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Issue.TitleMax);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(Issue.DescriptionMax);
    }
}

public sealed class ResolveIssueRequestValidator : AbstractValidator<ResolveIssueRequest>
{
    public ResolveIssueRequestValidator() => RuleFor(x => x.Note).MaximumLength(Issue.NoteMax);
}

public sealed class ReopenIssueRequestValidator : AbstractValidator<ReopenIssueRequest>
{
    public ReopenIssueRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(Issue.NoteMax);
}

public sealed class ChangePriorityRequestValidator : AbstractValidator<ChangePriorityRequest>
{
    public ChangePriorityRequestValidator() => RuleFor(x => x.Priority).IsInEnum();
}

public sealed class IssueCommentRequestValidator : AbstractValidator<IssueCommentRequest>
{
    public IssueCommentRequestValidator() => RuleFor(x => x.Body).NotEmpty().MaximumLength(IssueComment.BodyMax);
}
