using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RtxHub;

internal sealed class SimpleBackendFixture : ISimpleActionsBackend
{
    internal readonly HashSet<string> Managed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> Running=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> Residual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Ensured=new List<string>(),Launched=new List<string>(),Restored=new List<string>(),Videos=new List<string>();
    internal bool FailPrepare,FailEnsure,FailLaunch,RejectFrameGen;internal string FailRestore,VideoPlayer;
    internal readonly List<int> Multipliers=new List<int>();
    internal int PrepareCount,StopCalls;
    public string Prepare(Action<string> progress){PrepareCount++;if(FailPrepare)throw new InvalidOperationException("fixture package failure");return "prepared";}
    public string EnsureGame(string exe){if(FailEnsure)throw new InvalidOperationException("fixture install failure");Managed.Add(exe);Ensured.Add(exe);return "installed";}
    public void ValidateFrameGeneration(string exe,int multiplier){if(multiplier>1&&RejectFrameGen)throw new InvalidOperationException("unsupported fixture FG");}
    public string ConfigureFrameGeneration(string exe,int multiplier,Action<string> progress){Multipliers.Add(multiplier);return "FG request recorded";}
    public void LaunchGame(string exe){if(FailLaunch)throw new InvalidOperationException("fixture launch failure");Launched.Add(exe);}
    public SimpleVideoLaunch PlayVideo(string file){Videos.Add(file);Managed.Add(VideoPlayer);return new SimpleVideoLaunch{Executable=VideoPlayer,Message="video requested"};}
    public SimpleVideoLaunch PlayVideos(string[] files){foreach(string file in files)Videos.Add(file);Managed.Add(VideoPlayer);return new SimpleVideoLaunch{Executable=VideoPlayer,Message="playlist requested"};}
    public SimpleVideoLaunch OpenPlayer(){return PlayVideos(new string[0]);}
    public SimpleVideoLaunch CaptureBrowser(IntPtr window){return new SimpleVideoLaunch{Executable=VideoPlayer,Message="capture requested"};}
    public string[] ManagedTargets(){var list=new string[Managed.Count];Managed.CopyTo(list);return list;}
    public bool IsRunning(string exe){return Running.Contains(exe);}
    public bool HasCoreFiles(string exe){return Residual.Contains(exe);}
    public string Restore(string exe){if(exe==FailRestore)throw new InvalidOperationException("fixture busy file");Restored.Add(exe);if(!Residual.Contains(exe))Managed.Remove(exe);return "fixture preserved edited configuration";}
    public void StopOwnedPlayers(string[] targets,Action<string> progress){StopCalls++;}
}

internal static class SimpleActionsTests
{
    static int checks;
    static void Assert(bool value,string message){checks++;if(!value)throw new Exception("FAIL: "+message);}
    static void Throws(Action action,string message){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Assert(failed,message);}
    static string Touch(string root,string relative){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"inert fixture");return Path.GetFullPath(path);}
    static string Pending(string root,string name){return Path.Combine(root,name,"pending.xml");}
    static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"SimpleActions-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string game=Touch(root,"game/game.exe"),other=Touch(root,"other/other.exe"),video=Touch(root,"movie.mp4"),player=Touch(root,"private/mpv.exe");
            var b=new SimpleBackendFixture{VideoPlayer=player};var service=new SimpleActionService(Pending(root,"first"),b);
            service.Open(game,null);
            Assert(b.PrepareCount==1&&b.Ensured.Contains(game)&&b.Launched.Count==1,"selecting an EXE prepares, installs and requests launch");
            Assert(b.Videos.Count==0,"game selection never uses the video route");
            service.Open(video,null);Assert(b.Videos.Count==1&&b.Videos[0]==video&&b.Launched.Count==1,"video selection uses the private player without launching the game route");
            Throws(delegate{service.Open(Touch(root,"not-video.txt"),null);},"unsupported selection is rejected before installation");
            string episode=Touch(root,"second.mkv");service.OpenMany(new[]{video,episode,video},null);
            Assert(b.Videos.Count==3&&b.Videos[1]==video&&b.Videos[2]==episode,"multi-video selection preserves order and removes duplicates");
            int beforePrepare=b.PrepareCount;
            Throws(delegate{service.OpenMany(new[]{video,game},null);},"games cannot be mixed into video playlists");
            Assert(b.PrepareCount==beforePrepare,"invalid playlist is rejected before package writes");
            Throws(delegate{service.Open(Touch(root,"browser/chrome.exe"),null);},"browser executable cannot be treated as a game injection target");
            Assert(b.PrepareCount==beforePrepare,"browser selection is rejected before preparing components");
            service.Open(game,6,null);Assert(b.Multipliers.Contains(6),"requested 6x reaches the game FG backend");
            beforePrepare=b.PrepareCount;b.RejectFrameGen=true;
            Throws(delegate{service.Open(other,6,null);},"unsupported native FG blocks launch");
            Assert(b.PrepareCount==beforePrepare&&!b.Launched.Contains(other),"FG support is checked before preparing or installing components");
            b.RejectFrameGen=false;Throws(delegate{service.Open(video,6,null);},"video cannot silently receive game-only MFG");
            Assert(b.Multipliers.Contains(1),"default game route explicitly withdraws extra MFG");
            string[] union=NativeSimpleActionsBackend.MergeTargets(new[]{game},new[]{game,other});Assert(union.Length==2,"cancel combines NR and FG records without duplicate targets");

            var f=new SimpleBackendFixture{FailPrepare=true};var failService=new SimpleActionService(Pending(root,"fail"),f);
            Throws(delegate{failService.Open(game,null);},"package preparation failure reaches the caller");Assert(f.Ensured.Count==0&&f.Launched.Count==0,"failed packages cannot install or launch");
            f.FailPrepare=false;f.FailEnsure=true;Throws(delegate{failService.Open(game,null);},"installation failure reaches the caller");Assert(f.Launched.Count==0,"failed installation never launches the game");
            f.FailEnsure=false;f.FailLaunch=true;Throws(delegate{failService.Open(game,null);},"launch failure reaches the caller without a success claim");

            var deferred=new SimpleBackendFixture();deferred.Managed.Add(game);deferred.Running.Add(game);string state=Pending(root,"deferred");
            var d=new SimpleActionService(state,deferred);d.CancelAll(null);
            Assert(d.HasPendingCancellation&&File.Exists(state)&&deferred.Restored.Count==0,"running games persist cancellation without restoring loaded files");
            var resumed=new SimpleActionService(state,deferred);Assert(resumed.PollPending(null)==""&&deferred.Restored.Count==0,"unchanged running targets poll quietly");
            DateTime oldWrite=new DateTime(2001,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(state,oldWrite);resumed.PollPending(null);
            Assert(File.GetLastWriteTimeUtc(state)==oldWrite,"unchanged polls do not rewrite the durable pending list every three seconds");
            deferred.Running.Clear();string completion=resumed.PollPending(null);
            Assert(deferred.Restored.Contains(game)&&!resumed.HasPendingCancellation&&!File.Exists(state),"pending cancellation survives service recreation and completes after exit");Assert(completion.Length>0,"completed deferred restoration produces visible status");

            var multiple=new SimpleBackendFixture();multiple.Managed.Add(game);multiple.Managed.Add(other);multiple.Running.Add(game);multiple.Running.Add(other);string multipleState=Pending(root,"multiple");
            var ms=new SimpleActionService(multipleState,multiple);ms.CancelAll(null);ms.Open(game,null);
            Assert(ms.HasPendingCancellation,"explicit open does not clear another game's pending cancellation");multiple.Running.Clear();ms.PollPending(null);
            Assert(multiple.Restored.Contains(other)&&!multiple.Restored.Contains(game),"successful explicit reopen clears only its own pending target");

            var retained=new SimpleBackendFixture();retained.Managed.Add(game);retained.Residual.Add(game);var rs=new SimpleActionService(Pending(root,"retained"),retained);string retainedResult=rs.CancelAll(null);
            Assert(retained.Restored.Count==1&&!rs.HasPendingCancellation,"changed third-party files are reported without endless automated cleanup");Assert(retainedResult.Contains("保留"),"preserved files remain explicit in cancellation result");

            var errors=new SimpleBackendFixture{FailRestore=game};errors.Managed.Add(game);errors.Managed.Add(other);var es=new SimpleActionService(Pending(root,"errors"),errors);es.CancelAll(null);
            Assert(errors.Restored.Contains(other)&&es.HasPendingCancellation,"one blocked restore does not prevent cancellation of other managed targets");errors.FailRestore=null;es.PollPending(null);Assert(errors.Restored.Contains(game)&&!es.HasPendingCancellation,"transient failed restore retries through pending state");

            string unknown=Touch(root,"unknown/unknown.exe"),foreignState=Pending(root,"foreign");Directory.CreateDirectory(Path.GetDirectoryName(foreignState));
            File.WriteAllText(foreignState,"<pending version=\"1\"><target exe=\""+System.Security.SecurityElement.Escape(unknown)+"\"/></pending>");
            var untrusted=new SimpleBackendFixture();var us=new SimpleActionService(foreignState,untrusted);us.PollPending(null);
            Assert(untrusted.Restored.Count==0&&!us.HasPendingCancellation,"pending XML cannot authorize an unmanaged target");

            string renderer=Touch(root,"wwm/yysls_medium/Engine/Binaries/Win64rh/yysls.exe");string sibling=Touch(root,"wwm/yysls_medium/Engine/Binaries/Win64r/yysls.exe");string launcher=Touch(root,"wwm/Win32/deploy/launcher.exe");
            var wwm=new SimpleBackendFixture();new SimpleActionService(Pending(root,"wwm-state"),wwm).Open(renderer,null);
            Assert(wwm.Ensured.Contains(renderer)&&wwm.Ensured.Contains(sibling)&&wwm.Ensured.Count==2,"known renderer-family selection prepares both existing variants");
            Assert(wwm.Launched.Count==1&&wwm.Launched[0]==launcher,"known renderer-family launches its existing sibling client launcher");
            string privateRoot=Path.Combine(root,"owned-player");string safePlayer=Touch(privateRoot,"balanced/mpv.exe");
            Assert(NativeSimpleActionsBackend.IsPrivatePlayerPath(safePlayer,privateRoot),"an exact app-owned preset player path may be closed");
            Assert(!NativeSimpleActionsBackend.IsPrivatePlayerPath(Touch(root,"owned-player-foreign/balanced/mpv.exe"),privateRoot),"a shared path prefix never authorizes closing another player");
            Assert(!NativeSimpleActionsBackend.IsPrivatePlayerPath(Touch(privateRoot,"balanced/game.exe"),privateRoot),"an arbitrary executable inside the player tree is never a close target");
            string blocked=Touch(root,"cannot-store-state");var blockedBackend=new SimpleBackendFixture();blockedBackend.Managed.Add(game);
            Throws(delegate{new SimpleActionService(Path.Combine(blocked,"pending.xml"),blockedBackend).CancelAll(null);},"failed durable cancellation intent gives a useful application error");
            Assert(blockedBackend.Restored.Count==0,"failure to persist cancellation prevents restoration from starting");
            string dtdState=Pending(root,"dtd");Directory.CreateDirectory(Path.GetDirectoryName(dtdState));File.WriteAllText(dtdState,"<!DOCTYPE pending SYSTEM 'file:///untrusted'><pending version=\"1\"><target exe=\""+System.Security.SecurityElement.Escape(game)+"\"/></pending>");
            var dtdBackend=new SimpleBackendFixture();dtdBackend.Managed.Add(game);
            Throws(delegate{new SimpleActionService(dtdState,dtdBackend).PollPending(null);},"pending data rejects DTDs before any restoration");Assert(dtdBackend.Restored.Count==0,"malformed pending data cannot trigger cleanup");
            Console.WriteLine("PASS: "+checks+" simple-action orchestration checks; no game or native DLL executed.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{string full=Path.GetFullPath(root);if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(full,true);}
    }
}

