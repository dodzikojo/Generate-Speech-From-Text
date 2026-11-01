namespace IssueLab.VoiceBench.Application;

public sealed record Utf16TextEdit(int Start, int Length, string Text);

public sealed record EditorDraftSnapshot(
    string SegmentId, string Text, string? Directions, string Revision, bool IsDirty,
    bool CanUndo, bool CanRedo);

/// <summary>The same revisioned draft is used by the operator and desktop automation.</summary>
public sealed class LiveEditorDraft
{
    private readonly List<(string Text, string? Directions)> _history = [];
    private int _cursor;
    private (string Text, string? Directions) _saved;
    private string _revision = Guid.NewGuid().ToString("N");

    public LiveEditorDraft(string segmentId, string text, string? directions = null)
    {
        SegmentId = segmentId;
        _saved = (text, directions);
        _history.Add(_saved);
    }

    public string SegmentId { get; }
    public EditorDraftSnapshot Snapshot => new(SegmentId, Current.Text, Current.Directions, _revision,
        Current != _saved, _cursor > 0, _cursor < _history.Count - 1);
    private (string Text, string? Directions) Current => _history[_cursor];

    public EditorDraftSnapshot Replace(string expectedRevision, string text)
    {
        RequireRevision(expectedRevision);
        ArgumentNullException.ThrowIfNull(text);
        return Push((text, Current.Directions));
    }

    public EditorDraftSnapshot ApplyEdits(string expectedRevision, IReadOnlyList<Utf16TextEdit> edits)
    {
        RequireRevision(expectedRevision);
        ArgumentNullException.ThrowIfNull(edits);
        string text = Current.Text;
        Utf16TextEdit[] ordered = edits.OrderBy(edit => edit.Start).ThenBy(edit => edit.Length).ToArray();
        int end = -1;
        foreach (Utf16TextEdit edit in ordered)
        {
            if (edit.Text is null || edit.Start < 0 || edit.Length < 0 || edit.Start > text.Length - edit.Length)
                throw new AutomationException("editor.invalid-range", "Text edits must use valid UTF-16 ranges.");
            if (edit.Start < end || (edit.Start == end && edit.Length == 0 && ordered.Count(item => item.Start == edit.Start) > 1))
                throw new AutomationException("editor.overlapping-edits", "Text edits must not overlap.");
            RequireBoundary(text, edit.Start);
            RequireBoundary(text, edit.Start + edit.Length);
            end = edit.Start + edit.Length;
        }
        foreach (Utf16TextEdit edit in ordered.Reverse())
            text = string.Concat(text.AsSpan(0, edit.Start), edit.Text, text.AsSpan(edit.Start + edit.Length));
        return Push((text, Current.Directions));
    }

    public EditorDraftSnapshot SetDirections(string expectedRevision, string? directions)
    {
        RequireRevision(expectedRevision);
        return Push((Current.Text, directions));
    }

    public EditorDraftSnapshot Undo(string expectedRevision)
    {
        RequireRevision(expectedRevision);
        if (_cursor == 0) throw new AutomationException("editor.no-undo", "The live editor has no change to undo.");
        _cursor--;
        AdvanceRevision();
        return Snapshot;
    }

    public EditorDraftSnapshot Redo(string expectedRevision)
    {
        RequireRevision(expectedRevision);
        if (_cursor == _history.Count - 1) throw new AutomationException("editor.no-redo", "The live editor has no change to redo.");
        _cursor++;
        AdvanceRevision();
        return Snapshot;
    }

    public void MarkSaved() => _saved = Current;

    public EditorDraftSnapshot Reload(string text, string? directions = null)
    {
        _saved = (text, directions);
        _history.Clear();
        _history.Add(_saved);
        _cursor = 0;
        AdvanceRevision();
        return Snapshot;
    }

    private EditorDraftSnapshot Push((string Text, string? Directions) value)
    {
        if (value == Current) return Snapshot;
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        _history.Add(value);
        _cursor++;
        AdvanceRevision();
        return Snapshot;
    }

    public void RequireRevision(string expectedRevision)
    {
        if (!string.Equals(expectedRevision, _revision, StringComparison.Ordinal))
            throw new AutomationException("editor.revision-conflict", "The live editor changed. Read the current draft before editing.", new { currentRevision = _revision });
    }

    private void AdvanceRevision() => _revision = Guid.NewGuid().ToString("N");

    public static void RequireBoundary(string text, int position)
    {
        if (position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]))
            throw new AutomationException("editor.invalid-range", "A UTF-16 range cannot split a surrogate pair.");
    }
}
