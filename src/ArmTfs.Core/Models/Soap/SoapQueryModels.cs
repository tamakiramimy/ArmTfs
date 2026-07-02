namespace ArmTfs.Core.Models.Soap;

/// <summary>
/// QueryItems 返回的单个 TFVC 项（文件或文件夹）。
/// </summary>
public sealed record SoapItem(
    string ServerPath,
    bool IsFolder,
    int ChangesetId,
    long ContentLength,
    string? HashValue,
    string? DownloadUrl,
    DateTimeOffset? CheckinDate);

/// <summary>
/// QueryHistory 返回的单个 changeset 摘要。
/// </summary>
public sealed record SoapChangeset(
    int ChangesetId,
    string? Author,
    string? AuthorUniqueName,
    DateTimeOffset? CreatedDate,
    string? Comment);

/// <summary>
/// QueryChangesForChangeset 返回的单个文件变更。
/// </summary>
public sealed record SoapChangesetChange(
    string ServerPath,
    bool IsFolder,
    int ItemChangesetVersion,
    string ChangeType,
    IReadOnlyList<SoapMergeSourceInfo> MergeSources);

/// <summary>
/// 合并来源信息（来自 SOAP MergeSources/MergeSource 节点）。
/// </summary>
public sealed record SoapMergeSourceInfo(
    string ServerItem,
    int? VersionFrom,
    int? VersionTo,
    bool IsRename);

/// <summary>
/// QueryLabels 返回的单个 TFVC Label 信息。
/// </summary>
public sealed record SoapLabel(
    string Name,
    int LabelId,
    string? Scope,
    string? Owner,
    DateTimeOffset? Date,
    string? Comment);

/// <summary>
/// QueryShelvesets 返回的单个 Shelveset 信息。
/// </summary>
public sealed record SoapShelveset(
    string Name,
    string? Owner,
    string? OwnerDisplayName,
    DateTimeOffset? Date,
    string? Comment);

/// <summary>
/// QueryShelvesetChanges 返回的单个文件变更。
/// </summary>
public sealed record SoapShelvesetChange(
    string ServerPath,
    bool IsFolder,
    int ChangesetVersion,
    string ChangeType);

/// <summary>
/// QueryBranchObjects 返回的分支对象信息。
/// </summary>
public sealed record SoapBranchObject(
    string Path,
    string? Description,
    DateTimeOffset? DateCreated,
    string? ParentPath,
    bool IsDeleted,
    string? Owner,
    IReadOnlyList<string>? ChildPaths);
