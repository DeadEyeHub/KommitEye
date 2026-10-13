namespace KommitEye.Models;

public sealed record ProjectStatus(string ProjectId, string Status, string Commit, string Stage, bool Running, int? Remaining, DateTimeOffset? NextRun);
public sealed record ReviewJob(string Id, string ProjectId, string RequestKey, string Revision, string Kind,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt = null, string? Error = null);
public sealed record ServerEvent(string ProjectId, string Type, string Detail, DateTimeOffset OccurredAt);
public sealed record ProjectSnapshot(long Revision, IReadOnlyList<AppSettings> Projects, IReadOnlyList<ProjectStatus> Statuses);
