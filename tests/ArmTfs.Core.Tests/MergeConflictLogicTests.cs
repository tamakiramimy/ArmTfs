using System.Text;
using ArmTfs.Core.Client;
using ArmTfs.Core.Models;

namespace ArmTfs.Core.Tests;

/// <summary>
/// 验证 merge 冲突检测与内容比对的纯逻辑（不依赖真实 TFS 服务器）。
/// 覆盖问题2（冲突检测）与问题3（ContentEquals 规范化、双向 comment marker）。
/// </summary>
public class MergeConflictLogicTests
{
    // ─── IsContentConflict ─────────────────────────────────────────────────────

    [Fact]
    public void Conflict_when_both_sides_changed_differently()
    {
        var baseContent = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var targetCurrent = Encoding.UTF8.GetBytes("line1\nTARGET\nline3\n"); // target edited line2
        var sourceContent = Encoding.UTF8.GetBytes("line1\nSOURCE\nline3\n"); // source edited line2 differently

        Assert.True(TfvcClientService.IsContentConflict(targetCurrent, baseContent, sourceContent));
    }

    [Fact]
    public void No_conflict_when_target_unchanged_since_base()
    {
        var baseContent = Encoding.UTF8.GetBytes("line1\nline2\n");
        var sourceContent = Encoding.UTF8.GetBytes("line1\nCHANGED\n");

        // Only source changed; target still equals base → fast-forward, no conflict.
        Assert.False(TfvcClientService.IsContentConflict(baseContent, baseContent, sourceContent));
    }

    [Fact]
    public void No_conflict_when_target_already_matches_source()
    {
        var baseContent = Encoding.UTF8.GetBytes("line1\nline2\n");
        var same = Encoding.UTF8.GetBytes("line1\nCHANGED\n");

        // Both sides ended at the same content → no conflict.
        Assert.False(TfvcClientService.IsContentConflict(same, baseContent, same));
    }

    [Fact]
    public void No_conflict_when_any_input_is_null()
    {
        var bytes = Encoding.UTF8.GetBytes("x");
        Assert.False(TfvcClientService.IsContentConflict(null, bytes, bytes));
        Assert.False(TfvcClientService.IsContentConflict(bytes, null, bytes));
        Assert.False(TfvcClientService.IsContentConflict(bytes, bytes, null));
    }

    [Fact]
    public void Conflict_detection_normalizes_crlf_before_comparing()
    {
        // Target current (CRLF) equals base (LF) after normalization → target considered unchanged → no conflict.
        var baseContent = Encoding.UTF8.GetBytes("line1\nline2\n");
        var targetCurrent = Encoding.UTF8.GetBytes("line1\r\nline2\r\n");
        var sourceContent = Encoding.UTF8.GetBytes("line1\nCHANGED\n");

        Assert.False(TfvcClientService.IsContentConflict(targetCurrent, baseContent, sourceContent));
    }

    [Fact]
    public void Conflict_detection_treats_crlf_and_lf_target_as_matching_source()
    {
        // Source (LF) and target current (CRLF) are the same logical content → no conflict.
        var baseContent = Encoding.UTF8.GetBytes("base\n");
        var same = Encoding.UTF8.GetBytes("merged\n");
        var sameCrlf = Encoding.UTF8.GetBytes("merged\r\n");

        Assert.False(TfvcClientService.IsContentConflict(sameCrlf, baseContent, same));
    }

    // ─── ContentEquals (CRLF/LF normalization) ────────────────────────────────

    [Fact]
    public void ContentEquals_identical_bytes_are_equal()
    {
        var a = Encoding.UTF8.GetBytes("hello\nworld\n");
        Assert.True(TfvcClientService.ContentEquals(a, a));
    }

    [Fact]
    public void ContentEquals_treats_crlf_and_lf_as_equal()
    {
        var lf = Encoding.UTF8.GetBytes("a\nb\nc\n");
        var crlf = Encoding.UTF8.GetBytes("a\r\nb\r\nc\r\n");
        Assert.True(TfvcClientService.ContentEquals(lf, crlf));
        Assert.True(TfvcClientService.ContentEquals(crlf, lf));
    }

    [Fact]
    public void ContentEquals_detects_real_difference()
    {
        var a = Encoding.UTF8.GetBytes("a\nb\n");
        var b = Encoding.UTF8.GetBytes("a\nB\n");
        Assert.False(TfvcClientService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_handles_mixed_line_endings()
    {
        var a = Encoding.UTF8.GetBytes("a\r\nb\nc\r\nd\n");
        var b = Encoding.UTF8.GetBytes("a\nb\r\nc\nd\r\n");
        Assert.True(TfvcClientService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_empty_arrays_are_equal()
    {
        Assert.True(TfvcClientService.ContentEquals(Array.Empty<byte>(), Array.Empty<byte>()));
    }

    // ─── MergeCommentMarker (bidirectional recognition) ────────────────────────

    [Fact]
    public void Marker_round_trips_source_changeset_target_paths()
    {
        var marker = MergeCommentMarker.Build("$/Proj/Main", 213400, "$/Proj/Feature");
        var parsed = MergeCommentMarker.Parse(marker);

        Assert.NotNull(parsed);
        Assert.Equal("$/Proj/Main", parsed!.SourcePath);
        Assert.Equal(213400, parsed.SourceChangesetId);
        Assert.Equal("$/Proj/Feature", parsed.TargetPath);
    }

    [Fact]
    public void Marker_parses_from_comment_with_surrounding_text()
    {
        var comment = "Merge cs#213440 from $/Proj/Feature to $/Proj/Main [arm-tfs-merge:source=$/Proj/Feature;cs=213440;target=$/Proj/Main]";
        var parsed = MergeCommentMarker.Parse(comment);

        Assert.NotNull(parsed);
        Assert.Equal("$/Proj/Feature", parsed!.SourcePath);
        Assert.Equal(213440, parsed.SourceChangesetId);
        Assert.Equal("$/Proj/Main", parsed.TargetPath);
    }

    /// <summary>
    /// 问题3 双向 marker：源分支上一个"从目标合并过来"的 changeset，其 comment marker 的
    /// source 应为目标路径、target 应为源路径。候选过滤据此识别并排除反向合并。
    /// 此测试验证 Parse 能正确还原这种反向 marker 的字段，使过滤逻辑可判定。
    /// </summary>
    [Fact]
    public void Marker_reverse_merge_from_target_is_parseable_for_bidirectional_filter()
    {
        // A "trunk → branch" merge recorded on the branch (source) side.
        // Here the merge-source recorded in the marker is the target path (trunk),
        // and the merge-target is the source path (branch) — i.e. content came FROM target.
        var sourceBranch = "$/Proj/Feature";
        var targetBranch = "$/Proj/Main";
        var reverseMarker = MergeCommentMarker.Build(targetBranch, 213440, sourceBranch);
        var parsed = MergeCommentMarker.Parse(reverseMarker);

        Assert.NotNull(parsed);
        // The candidate filter checks: IsSameOrDescendantPath(targetPath, ownMarker.SourcePath)
        //   && IsSameOrDescendantPath(sourcePath, ownMarker.TargetPath)
        Assert.Equal(targetBranch, parsed!.SourcePath);  // marker source == target branch
        Assert.Equal(sourceBranch, parsed.TargetPath);   // marker target == source branch
        Assert.Equal(213440, parsed.SourceChangesetId);
    }

    [Fact]
    public void Marker_parse_returns_null_for_missing_or_malformed()
    {
        Assert.Null(MergeCommentMarker.Parse(null));
        Assert.Null(MergeCommentMarker.Parse(""));
        Assert.Null(MergeCommentMarker.Parse("just a normal checkin comment"));
        Assert.Null(MergeCommentMarker.Parse("[arm-tfs-merge:source=$/Proj;target=$/Proj]")); // no cs
    }
}
