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
