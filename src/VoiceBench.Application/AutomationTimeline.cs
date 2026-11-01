using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;

namespace IssueLab.VoiceBench.Application;

public sealed partial class AutomationDispatcher
{
    internal static readonly IReadOnlyDictionary<string, TimelineEditOperation> TimelineOperations = new Dictionary<string, TimelineEditOperation>
    {
        ["track.create"]=TimelineEditOperation.AddTrack,["track.update"]=TimelineEditOperation.UpdateTrack,
        ["track.delete"]=TimelineEditOperation.DeleteTrack,["track.reorder"]=TimelineEditOperation.ReorderTrack,
        ["clip.add"]=TimelineEditOperation.AddClip,["clip.update"]=TimelineEditOperation.UpdateClip,
        ["clip.move"]=TimelineEditOperation.MoveClip,["clip.trim"]=TimelineEditOperation.TrimClip,
        ["clip.split"]=TimelineEditOperation.SplitClip,["clip.duplicate"]=TimelineEditOperation.DuplicateClip,
        ["clip.delete"]=TimelineEditOperation.DeleteClip,["close-gap"]=TimelineEditOperation.CloseGap,
        ["envelope.set"]=TimelineEditOperation.SetEnvelope,["marker.set"]=TimelineEditOperation.SetMarker,
        ["marker.delete"]=TimelineEditOperation.DeleteMarker,["end.set"]=TimelineEditOperation.SetEndMarker,
        ["segment.omit"]=TimelineEditOperation.OmitSegment,["segment.include"]=TimelineEditOperation.IncludeSegment,
        ["layout.set"]=TimelineEditOperation.SetLayoutMode,["replacement.accept"]=TimelineEditOperation.AcceptReplacement,
        ["replacement.reject"]=TimelineEditOperation.RejectReplacement,
    };
    private static AudioTimelineScope TimelineScope(JsonElement args) => Optional(args,"scope") is string value
        ? Enum.TryParse<AudioTimelineScope>(value,true,out var scope)&&Enum.IsDefined(scope)?scope:throw new AutomationException("arguments.invalid","Scope must be Chapter or Compilation.")
        : Optional(args,"chapterId") is null?AudioTimelineScope.Compilation:AudioTimelineScope.Chapter;

    private async Task<object?> ExecuteTimelineAsync(string command, JsonElement args, CancellationToken ct)
    {
        string root=RequireRoot();
        var service=new AudioTimelineService(_projects,_history);
        var scope=TimelineScope(args);
        string? chapter=Optional(args,"chapterId");
        if(command.StartsWith("mix.history.",StringComparison.Ordinal))
        {
            var state=await _history.GetStateAsync(root,ct).ConfigureAwait(false);
            if(command=="mix.history.get")return state;
            bool undo=command=="mix.history.undo";
            var next=undo?state.NextUndo:state.NextRedo;
            if(next?.Label!="Edit audio timeline")throw new AutomationException("mix.history-scope-conflict","The next saved action is not an audio timeline edit. Use saved project history for other actions.");
            var record=await Coordinator.ExecuteAsync(()=>undo?_history.UndoAsync(root,ct):_history.RedoAsync(root,ct),ct).ConfigureAwait(false);
            await ReloadAsync(ct).ConfigureAwait(false);
            return record;
        }
        if(command=="mix.get") return await service.GetAsync(root,scope,chapter,ct).ConfigureAwait(false);
        if(command=="mix.list") return (await ReloadAsync(ct).ConfigureAwait(false)).AudioTimelines.Select(t=>new{t.TimelineId,t.Scope,t.ChapterId,t.Revision}).ToArray();
        if(command=="mix.validate") return await service.ValidateAsync(root,scope,chapter,ct).ConfigureAwait(false);
        if(command=="mix.plan") return await service.BuildPlanAsync(root,scope,chapter,ct).ConfigureAwait(false);
        if(command=="mix.replacements") return (await service.GetAsync(root,scope,chapter,ct).ConfigureAwait(false)).ReplacementProposals;
        if(command=="mix.configure")
        {
            var standard=Deserialize<MixOutputStandard>(args,"outputStandard");
            if(standard.SampleRate is <8000 or >192000||standard.Channels is <1 or >2||standard.PcmBitsPerSample is not(16 or 24)
                ||!double.IsFinite(standard.IntegratedLufs)||standard.IntegratedLufs is <-36 or >-9
                ||!double.IsFinite(standard.TruePeakDbtp)||standard.TruePeakDbtp is <-9 or >0)
                throw new AutomationException("arguments.invalid","Use 8–192 kHz, mono/stereo, 16/24-bit PCM, -36 to -9 LUFS and -9 to 0 dBTP.");
            if(!DateTimeOffset.TryParse(Required(args,"expectedProjectRevision"),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var expected))
                throw new AutomationException("arguments.invalid","Supply updatedAt from project.get as expectedProjectRevision.");
            return await UpdateAsync("Configure mixed output",p=>p.UpdatedAt==expected?p with {MixOutputStandard=standard}
                :throw new AutomationException("project.revision-conflict","Project settings changed; read project.get again."),ct).ConfigureAwait(false);
        }
        if(command=="assets.list")
        {
            var assets=AudioTimelineRules.RegisterRenderAssets(await ReloadAsync(ct).ConfigureAwait(false)).AudioAssets;
            int offset=Math.Max(0,Int(args,"offset")),count=Math.Clamp(Int(args,"count",200),1,1000);
            return new { assets=assets.Skip(offset).Take(count).ToArray(),offset,total=assets.Count,nextOffset=offset+count<assets.Count?(int?)(offset+count):null };
        }
        if(command=="assets.waveform") return await DesktopAsync(command,args,ct).ConfigureAwait(false);
        IReadOnlyList<TimelineEdit> edits;
        if(command=="mix.edit") edits=Deserialize<List<TimelineEdit>>(args,"edits");
        else if(command.StartsWith("mix.",StringComparison.Ordinal)&&TimelineOperations.TryGetValue(command[4..],out var operation))
        {
            var editArgs=JsonNode.Parse(args.GetRawText())!.AsObject();
            foreach(string key in new[]{"scope","chapterId","expectedRevision"})editArgs.Remove(key);
            editArgs["operation"]=operation.ToString();
            edits=[editArgs.Deserialize<TimelineEdit>(AutomationJson.Options)!];
        }
        else throw new AutomationException("command.unknown","Unknown audio timeline command.");
        if(edits.Count is <1 or >1000)throw new AutomationException("arguments.invalid","An audio edit batch must contain 1–1000 operations.");
        AudioTimelineDocument updated=await Coordinator.ExecuteAsync(()=>service.ApplyAsync(root,scope,chapter,args.GetProperty("expectedRevision").GetInt64(),edits,ct),ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
        return updated;
    }
}
