using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Application;

internal static class AudioTimelineCapabilities
{
    private sealed record Arguments
    {
        public AudioTimelineScope? Scope { get; init; }
        public string? ChapterId { get; init; }
        public long? ExpectedRevision { get; init; }
        public TimelineEdit[]? Edits { get; init; }
        public Guid? TrackId { get; init; }
        public Guid? ClipId { get; init; }
        public Guid? NewClipId { get; init; }
        public Guid? MarkerId { get; init; }
        public Guid? ProposalId { get; init; }
        public string? SegmentId { get; init; }
        public Guid? AssetId { get; init; }
        public AudioTrackDocument? Track { get; init; }
        public AudioClipDocument? Clip { get; init; }
        public TimelineMarker? Marker { get; init; }
        public int? Order { get; init; }
        public double? StartSeconds { get; init; }
        public double? SourceStartSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public double? SplitSeconds { get; init; }
        public VolumeEnvelopePoint[]? Envelope { get; init; }
        public TimelineLayoutMode? LayoutMode { get; init; }
        public bool? ConfirmedReview { get; init; }
        public string? SourcePath { get; init; }
        public string? Name { get; init; }
        public bool? Mp3 { get; init; }
        public bool? Stems { get; init; }
        public int? Offset { get; init; }
        public int? Count { get; init; }
        public bool? Enabled { get; init; }
        public double? Seconds { get; init; }
        public double? EndSeconds { get; init; }
        public double? Volume { get; init; }
        public Guid[]? SoloTrackIds { get; init; }
        public string? DeviceId { get; init; }
        public MixOutputStandard? OutputStandard { get; init; }
        public string? ExpectedProjectRevision { get; init; }
    }
    public static IReadOnlyList<AutomationCapability> All { get; } = Build();
    private static IReadOnlyList<AutomationCapability> Build()
    {
        List<AutomationCapability> result=[];
        void Add(string command,string description,bool readOnly,string fields="",string required="",bool destructive=false)
        {
            JsonNode schema=AutomationJson.Options.GetJsonSchemaAsNode(typeof(Arguments));
            schema["type"]="object";
            string[] keys=fields.Split(',',StringSplitOptions.RemoveEmptyEntries);
            var properties=schema["properties"]!.AsObject();
            foreach(string key in properties.Select(p=>p.Key).Except(keys).ToArray())properties.Remove(key);
            schema["required"]=JsonSerializer.SerializeToNode(required.Split(',',StringSplitOptions.RemoveEmptyEntries));
            schema["additionalProperties"]=false;
            result.Add(new(command,description,readOnly,destructive,JsonSerializer.SerializeToElement(schema,AutomationJson.Options)));
        }
        const string target="scope,chapterId";
        Add("mix.get","Read the shared audio arrangement and current revision. Time edits use seconds on a 48 kHz frame grid.",true,target);
        Add("mix.list","List available project timelines and revisions.",true);
        Add("mix.validate","Inspect source, approval, overlap and missing-narration export blockers.",true,target);
        Add("mix.plan","Read the frozen mix graph and retained-source transcript without exporting audio.",true,target);
        Add("mix.replacements","Read pending take replacements for explicit review.",true,target);
        Add("mix.history.get","Read saved project history for the mix editor.",true);
        Add("mix.history.undo","Undo the latest saved action only if it is an audio timeline edit; preserve the live script draft.",false);
        Add("mix.history.redo","Redo the next saved action only if it is an audio timeline edit; preserve the live script draft.",false);
        Add("mix.edit","Apply a typed atomic audio edit batch against one revision. One undo action; no canonical script splitting or source-file changes.",false,target+",expectedRevision,edits","expectedRevision,edits");
        var edits=new (string Command,string Fields,string Required,string Description,bool Destructive)[]{
            ("track.create","track","track","Create a narration/music/effects track.",false),
            ("track.update","trackId,track","trackId,track","Set track gain, balance, lock, mute or ducking.",false),
            ("track.delete","trackId","trackId","Delete a track and its references through recoverable history; retain original audio.",true),
            ("track.reorder","trackId,order","trackId,order","Reorder a track.",false),
            ("clip.add","clip","clip","Place an exact registered audio asset.",false),
            ("clip.update","clipId,clip","clipId,clip","Set clip gain, balance, fades or looping.",false),
            ("clip.move","clipId,trackId,startSeconds","clipId,startSeconds","Move a clip without altering its source script.",false),
            ("clip.trim","clipId,sourceStartSeconds,durationSeconds","clipId,sourceStartSeconds,durationSeconds","Trim audio; spoken cuts require reliable word boundaries.",false),
            ("clip.split","clipId,splitSeconds,newClipId","clipId,splitSeconds","Split an audio clip and retain both halves; this does not split a script segment.",false),
            ("clip.duplicate","clipId,newClipId,startSeconds","clipId","Duplicate an immutable audio reference.",false),
            ("clip.delete","clipId","clipId","Lift a clip leaving a gap and keeping original audio.",true),
            ("close-gap","clipId","clipId","Close the gap preceding a selected clip atomically; locked or unsafe material blocks the operation.",false),
            ("envelope.set","clipId,envelope","clipId,envelope","Replace clip volume automation as one undo action.",false),
            ("marker.set","marker","marker","Add or update a placed marker.",false),
            ("marker.delete","markerId","markerId","Remove a marker.",true),
            ("end.set","durationSeconds","durationSeconds","Set the timeline end marker.",false),
            ("segment.omit","segmentId","segmentId","Explicitly omit narration without deleting its script.",false),
            ("segment.include","segmentId","segmentId","Include an omitted segment for placement.",false),
            ("layout.set","layoutMode","layoutMode","Choose automatic or manual narration layout.",false),
            ("replacement.accept","proposalId,confirmedReview","proposalId,confirmedReview","Accept a reviewed replacement whose source ranges can transfer safely.",false),
            ("replacement.reject","proposalId","proposalId","Keep the pinned take and reject a replacement.",false)
        };
        foreach(var e in edits)Add("mix."+e.Command,e.Description,false,target+",expectedRevision,"+e.Fields,"expectedRevision,"+e.Required,e.Destructive);
        Add("mix.configure","Set mixed output standards using the project's updatedAt value from project.get as expectedProjectRevision.",false,"outputStandard,expectedProjectRevision","outputStandard,expectedProjectRevision");
        Add("assets.list","Read a bounded page of immutable asset metadata.",true,"offset,count");
        Add("assets.import","Queue a managed copy of local audio; segmentId registers imported narration. Import never uploads audio.",false,"sourcePath,segmentId","sourcePath");
        Add("assets.prepare","Queue hash-verified PCM/waveform cache preparation.",false,"assetId","assetId");
        Add("assets.waveform","Read up to 4096 waveform buckets with continuation.",true,"assetId,offset,count","assetId");
        Add("assets.relink","Explicitly restore a missing asset with matching bytes; import changed bytes as a new asset.",false,"assetId,sourcePath","assetId,sourcePath");
        Add("mix.export","Queue approved-source mixed WAV and transcript/cue/report outputs, with optional MP3 and pre-master stems.",false,target+",name,mp3,stems","name");
        foreach(string c in new[]{"get","play","pause","stop","devices"})Add("mix.playback."+c,"Control/read the shared mix transport: "+c+".",c is "get" or "devices",c=="play"?target:"");
        Add("mix.playback.seek","Seek the mix playback clock.",false,"seconds","seconds");
        Add("mix.playback.loop","Set a bounded playback loop.",false,"enabled,seconds,endSeconds","enabled");
        Add("mix.playback.monitor","Set monitor-only volume/solo; exports ignore these controls.",false,"volume,soloTrackIds");
        Add("mix.playback.device","Select a specific output device or Windows default.",false,"deviceId");
        return result.AsReadOnly();
    }
}
