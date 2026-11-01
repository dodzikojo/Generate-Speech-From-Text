using Xunit;

namespace IssueLab.VoiceBench.Application.Tests;

public sealed class LiveEditorDraftTests
{
    [Fact]
    public void EditsShareOneRevisionAndUndoIncludesDirections()
    {
        LiveEditorDraft draft = new("segment", "A😀 hello", "calm");
        string original = draft.Snapshot.Revision;
        var edited = draft.ApplyEdits(original, [new(4, 5, "world"), new(0, 1, "The")]);
        Assert.Equal("The😀 world", edited.Text);
        Assert.True(edited.IsDirty);
        var directed = draft.SetDirections(edited.Revision, "bright");
        Assert.Equal("bright", directed.Directions);
        var undo = draft.Undo(directed.Revision);
        Assert.Equal("calm", undo.Directions);
        Assert.Equal("The😀 world", undo.Text);
        var restored = draft.Undo(undo.Revision);
        Assert.False(restored.IsDirty);
        Assert.NotEqual(original, restored.Revision);
        Assert.Equal("A😀 hello", restored.Text);
        Assert.Equal("The😀 world", draft.Redo(restored.Revision).Text);
    }

    [Fact]
    public void StaleRevisionCannotMutateOrUndo()
    {
        LiveEditorDraft draft = new("s", "old");
        string stale = draft.Snapshot.Revision;
        var changed = draft.Replace(stale, "new");
        Assert.Equal("editor.revision-conflict", Assert.Throws<AutomationException>(() => draft.Replace(stale, "lost")).Code);
        Assert.Equal("editor.revision-conflict", Assert.Throws<AutomationException>(() => draft.Undo(stale)).Code);
        Assert.Equal(changed, draft.Snapshot);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 1)]
    [InlineData(10, 0)]
    public void InvalidUtf16RangesAreRejectedAtomically(int start, int length)
    {
        LiveEditorDraft draft = new("s", "A😀B");
        var before = draft.Snapshot;
        Assert.Throws<AutomationException>(() => draft.ApplyEdits(before.Revision, [new(start, length, "x")]));
        Assert.Equal(before, draft.Snapshot);
    }

    [Fact]
    public void OverlapsAndAmbiguousInsertionsAreRejected()
    {
        LiveEditorDraft draft = new("s", "abcdef");
        Assert.Throws<AutomationException>(() => draft.ApplyEdits(draft.Snapshot.Revision, [new(1, 3, "x"), new(2, 2, "y")]));
        Assert.Throws<AutomationException>(() => draft.ApplyEdits(draft.Snapshot.Revision, [new(1, 0, "x"), new(1, 0, "y")]));
        Assert.Equal("abcdef", draft.Snapshot.Text);
    }

    [Fact]
    public void SaveAndBranchingHistoryTrackActualContent()
    {
        LiveEditorDraft draft = new("s", "a");
        var b = draft.Replace(draft.Snapshot.Revision, "b");
        draft.MarkSaved();
        Assert.False(draft.Snapshot.IsDirty);
        var a = draft.Undo(b.Revision);
        Assert.True(a.IsDirty);
        var c = draft.Replace(a.Revision, "c");
        Assert.False(c.CanRedo);
        Assert.Throws<AutomationException>(() => draft.Redo(c.Revision));
        var reloaded = draft.Reload("external", "new directions");
        Assert.False(reloaded.IsDirty);
        Assert.False(reloaded.CanUndo);
    }
}
