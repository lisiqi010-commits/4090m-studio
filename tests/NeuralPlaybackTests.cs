using System;
using System.IO;
using RtxHub;
internal static class NeuralPlaybackTests
{
 static int Main(){string dir=Path.Combine(Path.GetTempPath(),"RtxHub-playback-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);try{
 string media=Path.Combine(dir,"测试 影片 & name.mp4");File.WriteAllBytes(media,new byte[]{1});
 string args=NeuralPlayback.BuildArguments(media);
 Assert(args.Contains("--no-config")&&args.Contains("--load-scripts=no"),"no user config or scripts");
 Assert(args.Contains("--gpu-context=d3d11")&&args.Contains("--d3d11-adapter=NVIDIA"),"DX11 NVIDIA contract");
 Assert(args.Contains("--target-trc=srgb")&&args.Contains("--target-prim=bt.709"),"explicit SDR rendering");
 Assert(args.EndsWith("-- "+VideoSupport.QuoteWindowsArgument(media)),"one safely quoted local movie, options terminated");
 string second=Path.Combine(dir,"第二集.mp4");File.WriteAllBytes(second,new byte[]{1});
 string playlist=NeuralPlayback.BuildPlaylistArguments(new[]{media,second,media});
 Assert(playlist.EndsWith("-- "+VideoSupport.QuoteWindowsArgument(media)+" "+VideoSupport.QuoteWindowsArgument(second)),"playlist preserves order and deduplicates paths");
 Assert(playlist.Contains("--osd-playing-msg=")&&playlist.Contains("playlist-pos-1"),"fallback player displays playlist position and keyboard instructions");
 bool mixed=false;try{NeuralPlayback.BuildPlaylistArguments(new[]{media,Path.Combine(dir,"absent.mp4")});}catch(FileNotFoundException){mixed=true;}Assert(mixed,"missing playlist entry rejects entire launch");
 bool empty=false;try{NeuralPlayback.BuildPlaylistArguments(new string[0]);}catch(ArgumentException){empty=true;}Assert(empty,"empty playlist rejected");
 string[] large=new string[150];for(int n=0;n<large.Length;n++){large[n]=Path.Combine(dir,new string('x',132)+n.ToString("D3")+".mp4");File.WriteAllBytes(large[n],new byte[]{1});}
 bool longArgs=false;try{NeuralPlayback.BuildPlaylistArguments(large);}catch(ArgumentException){longArgs=true;}Assert(longArgs,"overlong Windows command line rejected before preparing the player");
 Assert(!args.Contains("d3d11vpp")&&!args.Contains("--profile=high-quality"),"NR independent from VSR request");
 bool missing=false;try{NeuralPlayback.BuildArguments(Path.Combine(dir,"absent.mp4"));}catch(FileNotFoundException){missing=true;}Assert(missing,"missing file rejected");
 bool invalid=false;try{NeuralPlayback.PlayerDirectory(-1);}catch(ArgumentOutOfRangeException){invalid=true;}Assert(invalid,"invalid preset rejected");
 Assert(NeuralPlayback.PlayerDirectory(0)!=NeuralPlayback.PlayerDirectory(1),"presets use separate player configurations");
 string original=Path.Combine(dir,"original.dll"), changed=Path.Combine(dir,"changed.dll");File.WriteAllText(original,"trusted");File.WriteAllText(changed,"trusted");NeuralPlayback.VerifyPrivateFile(changed,original);
 File.WriteAllText(changed,"modified");bool modified=false;try{NeuralPlayback.VerifyPrivateFile(changed,original);}catch(InvalidOperationException){modified=true;}Assert(modified,"modified private payload cannot be launched");
 bool missingConfig=false;try{NeuralPlayback.VerifyPrivateConfig(Path.Combine(dir,"ReShade.ini"));}catch(InvalidOperationException){missingConfig=true;}Assert(missingConfig,"missing configuration cannot launch defaults");
 string config=Path.Combine(dir,"ReShade.ini");File.WriteAllText(config,"personal edits preserved");NeuralPlayback.VerifyPrivateConfig(config);
 Console.WriteLine("PASS: 17 NR playback contract checks; no process launched.");return 0;
 }catch(Exception ex){Console.WriteLine("FAIL: "+ex.Message);return 1;}finally{foreach(string f in Directory.GetFiles(dir))File.Delete(f);Directory.Delete(dir);}}
 static void Assert(bool b,string message){if(!b)throw new Exception(message);}
}


