using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using Xunit;

namespace VoiceBench.Application.Tests;

public sealed class TimelineAutomationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"VoiceBench-TimelineAutomation-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task EditsAreGuardedDeduplicatedAndUndoable()
    {
        using var dispatcher=Create();
        Assert.False((await Call(dispatcher,"project.create",new {projectRoot=Path.Combine(root,"project"),name="Mix"})).IsError);
        var read=await Call(dispatcher,"mix.get",new {scope="Compilation"});
        Assert.False(read.IsError,read.Message);
        long revision=read.Data.GetProperty("revision").GetInt64();
        var edit=new {scope="Compilation",expectedRevision=revision,track=new AudioTrackDocument {Name="Background",Kind=AudioTrackKind.Music}};
        var first=await Call(dispatcher,"mix.track.create",edit,"one-track");
        Assert.False(first.IsError,first.Message);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(first.Data,(await Call(dispatcher,"mix.track.create",edit,"one-track")).Data));
        Assert.Equal("mix.revision-conflict",(await Call(dispatcher,"mix.track.create",edit)).Code);
        Assert.False((await Call(dispatcher,"history.undo",new{})).IsError);
        var undone=await Call(dispatcher,"mix.get",new {scope="Compilation"});
        Assert.DoesNotContain(undone.Data.GetProperty("tracks").EnumerateArray(),t=>t.GetProperty("name").GetString()=="Background");
    }
    [Fact]
    public async Task InvalidBatchesDoNotCommitAndSchemasRejectSecrets()
    {
        using var dispatcher=Create();
        await Call(dispatcher,"project.create",new {projectRoot=Path.Combine(root,"project"),name="Mix"});
        var before=await Call(dispatcher,"mix.get",new {scope="Compilation"});
        var failed=await Call(dispatcher,"mix.edit",new {scope="Compilation",expectedRevision=before.Data.GetProperty("revision").GetInt64(),edits=new[]{
            new TimelineEdit {Operation=TimelineEditOperation.AddTrack,Track=new AudioTrackDocument {Name="Roll back",Kind=AudioTrackKind.Music}},
            new TimelineEdit {Operation=TimelineEditOperation.DeleteClip,ClipId=Guid.NewGuid()}
        }});
        Assert.True(failed.IsError);
        var after=await Call(dispatcher,"mix.get",new {scope="Compilation"});
        Assert.Equal(before.Data.GetProperty("revision").GetInt64(),after.Data.GetProperty("revision").GetInt64());
        Assert.Equal("arguments.invalid",(await Call(dispatcher,"assets.import",new {sourcePath="file.wav",apiKey="never-accepted"})).Code);
    }
    private AutomationDispatcher Create()=>new(recentProjects:new FileRecentProjectCatalog(catalogPath:Path.Combine(root,"recent.json")),automationStateDirectory:Path.Combine(root,"state"));
    private static Task<AutomationResult> Call(AutomationDispatcher d,string command,object args,string? id=null)=>d.ExecuteAsync(new(command,AutomationJson.Element(args),id));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
