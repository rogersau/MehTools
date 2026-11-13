using System.Text;
using JamesTools.Core.ConfigurationSnapshots;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
internal static class ConfigSnapshotTests
{
    public static Task Run()
    {
        var dir=Path.Combine(Path.GetTempPath(),"meh-config-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        void Check(bool ok){if(!ok)throw new Exception("Configuration snapshot invariant failed");}
        void Refuse(Action action){try{action();}catch(Exception){return;}throw new Exception("Expected refusal");}
        try
        {
            var path=Path.Combine(dir,"test.ini");var game=Path.Combine(dir,"game.exe");
            var original=new byte[]{239,187,191}.Concat(Encoding.UTF8.GetBytes("a=1\r\nb=2\r\n")).ToArray();File.WriteAllBytes(path,original);
            var store=new ConfigSnapshotStore(dir);store.Select(game,[path]);var snap=store.Create(game,"Before");
            snap.Files[0]=snap.Files[0] with {Hash="bad"};Check(store.Snapshots(game)[0].Files[0].Hash!="bad");
            store=new ConfigSnapshotStore(dir);var id=store.Snapshots(game)[0].Id;Check(store.Compare(game,id,path).Contains("added 0"));
            File.WriteAllText(path,"a=3\n");var plan=store.Preview(game,id,path);Check(plan.Difference.Contains("+ a=1") && plan.Difference.Contains("- a=3"));
            var changed=File.ReadAllBytes(path);var originalFiles=Directory.GetFiles(dir).Length;Refuse(()=>store.Restore(plan,()=>"active"));Check(File.ReadAllBytes(path).SequenceEqual(changed) && Directory.GetFiles(dir).Length==originalFiles);
            File.AppendAllText(path,"later");Refuse(()=>store.Restore(plan,()=>null));
            plan=store.Preview(game,id,path);var result=store.Restore(plan,()=>null);Check(result.Contains("verified") && File.ReadAllBytes(path).SequenceEqual(original));
            Check(Directory.GetFiles(dir,"*.bak").Any());
            File.WriteAllText(path,"commit should fail\n");plan=store.Preview(game,id,path);changed=File.ReadAllBytes(path);
            FileStream? locked=null;int guards=0;var backupsBefore=Directory.GetFiles(dir,"*.bak").Length;
            try { Refuse(()=>store.Restore(plan,()=> { if(++guards==2) locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); return null; })); }
            finally { locked?.Dispose(); }
            Check(guards==2 && File.ReadAllBytes(path).SequenceEqual(changed) && Directory.GetFiles(dir,"*.bak").Length==backupsBefore+1 && Directory.GetFiles(dir,"*.tmp").Length==0);
            Check(Directory.GetFiles(dir,"*.bak").Any(p=>File.ReadAllBytes(p).SequenceEqual(changed)));
            plan=store.Preview(game,id,path);guards=0;
            Refuse(()=>store.Restore(plan,()=> { if(++guards==2)File.WriteAllText(path,"concurrent edit");return null; }));Check(File.ReadAllText(path)=="concurrent edit");
            var snapshotRoot=Path.Combine(dir,"ConfigurationSnapshots");var manifestPath=Path.Combine(snapshotRoot,"manifest.json");var payloadCount=Directory.GetFiles(snapshotRoot,"*.bin").Length;var snapshotCount=store.Snapshots(game).Length;
            using(new FileStream(manifestPath,FileMode.Open,FileAccess.Read,FileShare.None))Refuse(()=>store.Create(game,"failed save"));
            Check(Directory.GetFiles(snapshotRoot,"*.bin").Length==payloadCount && store.Snapshots(game).Length==snapshotCount);
            var second=Path.Combine(dir,"second.cfg");File.WriteAllText(second,"ok");store.Select(game,[path,second]);File.Delete(second);Refuse(()=>store.Create(game,"partial read"));Check(Directory.GetFiles(snapshotRoot,"*.bin").Length==payloadCount);store.Select(game,[path]);
            store.Select(game,[]);Refuse(()=>store.Preview(game,id,path));Check(store.Snapshots(game).Length==1);
            store.Select(game,[path]);File.SetAttributes(path,FileAttributes.ReadOnly);Refuse(()=>store.Restore(store.Preview(game,id,path),()=>null));File.SetAttributes(path,FileAttributes.Normal);
            File.Delete(path);Refuse(()=>store.Compare(game,id,path));
            Refuse(()=>ConfigSnapshotStore.LocalPath(Path.Combine(dir,"..","evil.ini")));Refuse(()=>ConfigSnapshotStore.LocalPath(@"\\server\share\x.ini"));
            foreach(var forbidden in new[]{"//server/share/x.ini",@"C:\NUL.txt",@"C:\test.ini:stream",@"C:\test.ini.",@"C:\test.ini "})Refuse(()=>ConfigSnapshotStore.LocalPath(forbidden));
            Refuse(()=>ConfigSnapshotStore.Decode([255,0,254]));Refuse(()=>ConfigSnapshotStore.Decode([0,1]));
            Check(ConfigSnapshotStore.Decode(new byte[]{255,254,97,0}).Text=="a");
            Check(ConfigSnapshotStore.Difference(string.Join('\n',Enumerable.Repeat("a",10000)),string.Join('\n',Enumerable.Repeat("b",10000))).Length<65000);
            Check(ConfigSnapshotStore.ClosedGuard(new GameRule{ExecutablePath=game},[new RunningProcess(7,"game.exe",null)])!=null);
            Check(ConfigSnapshotStore.ClosedGuard(new GameRule{ExecutablePath=game},[])==null);
            Check(ConfigSnapshotStore.ClosedGuard(new GameRule{ExecutablePath=game,MatchMode="folder"},[new RunningProcess(1,"unknown.exe",null)])!=null);
            var report=Path.Combine(dir,"report.md");File.WriteAllText(report,"- Report format: 2\n## Sample timeline (CSV)\n```csv\nsample_start_s,sample_end_s,cpu_pct\n0,1,5\n```\n");store.Link(game,id,report);File.Delete(report);
            Check(new ConfigSnapshotStore(dir).Snapshots(game)[0].Reports.Single()==report);store.Link(game,id,report,true);
            var stale=new ConfigSnapshotStore(dir);store.Select(game,[]);Refuse(()=>stale.Select(game,[]));Check(new ConfigSnapshotStore(dir).Selection(game).Length==0);
            File.WriteAllText(path,"fresh");store.Select(game,[path]);plan=store.Preview(game,id,path);new ConfigSnapshotStore(dir).Select(game,[]);Refuse(()=>store.Restore(plan,()=>null));Check(File.ReadAllText(path)=="fresh");store=new ConfigSnapshotStore(dir);
            var target=Path.Combine(dir,"junction-target");var link=Path.Combine(dir,"junction-link");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"linked.ini"),"untouched");
            using(var process=new System.Diagnostics.Process { StartInfo=new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true } })
            {
                process.StartInfo.ArgumentList.Add("-NoProfile");process.StartInfo.ArgumentList.Add("-NonInteractive");process.StartInfo.ArgumentList.Add("-Command");
                process.StartInfo.ArgumentList.Add("New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' -ErrorAction Stop | Out-Null");
                process.Start();if(!process.WaitForExit(5000)){process.Kill(true);throw new Exception("Junction fixture timed out");}Check(process.ExitCode==0);
            }
            try { Refuse(()=>store.Select(game,[Path.Combine(link,"linked.ini")]));Refuse(()=>new ConfigSnapshotStore(link));Check(!Directory.Exists(Path.Combine(target,"ConfigurationSnapshots")));Check(File.ReadAllText(Path.Combine(target,"linked.ini"))=="untouched"); }
            finally { Directory.Delete(link); }
            var manifest=Path.Combine(dir,"ConfigurationSnapshots","manifest.json");var valid=File.ReadAllText(manifest);
            File.WriteAllText(manifest,valid.Replace("\"Version\":1","\"Version\":99"));Refuse(()=>new ConfigSnapshotStore(dir));Check(File.ReadAllText(manifest).Contains("99"));File.WriteAllText(manifest,valid);
            var payload=Directory.GetFiles(Path.Combine(dir,"ConfigurationSnapshots"),"*.bin")[0];File.WriteAllBytes(payload,[1]);Refuse(()=>new ConfigSnapshotStore(dir));Check(File.Exists(payload));
        }
        finally {Directory.Delete(dir,true);}
        return Task.CompletedTask;
    }
}
