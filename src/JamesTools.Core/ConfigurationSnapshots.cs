using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Diagnostics;

namespace JamesTools.Core.ConfigurationSnapshots;

public sealed record ConfigFile(Guid Payload, string Path, string Hash, int Size, string Encoding);
public sealed record ConfigSnapshot(Guid Id, string Game, string Name, DateTime CreatedUtc, ConfigFile[] Files, string[] Reports) { public string Label => $"{Name} • {CreatedUtc:u} • {Id.ToString("N")[..8]}"; }
public sealed record ConfigManifest(int Version, Dictionary<string, string[]> Selections, ConfigSnapshot[] Snapshots);
public sealed record RestorePlan(Guid Snapshot, string Game, string Path, string CurrentHash, string SnapshotHash, string Difference);

/// <summary>Explicit file allowlists and immutable byte payloads. Failed loads never become empty stores.</summary>
public sealed class ConfigSnapshotStore
{
    public const int MaxBytes = 1024 * 1024;
    readonly string root, manifest;
    string? manifestHash;
    ConfigManifest state;
    public ConfigSnapshotStore(string appDirectory)
    {
        root = LocalPath(System.IO.Path.Combine(appDirectory, "ConfigurationSnapshots"));
        Safe(root); Directory.CreateDirectory(root);
        manifest = System.IO.Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest) && File.Exists(manifest + ".bak")) throw new IOException("Snapshot manifest missing; backup retained at " + manifest + ".bak");
        Safe(manifest); if(File.Exists(manifest) && new FileInfo(manifest).Length > 16 * MaxBytes) throw new IOException("Snapshot manifest exceeds supported size.");
        var loaded=File.Exists(manifest) ? File.ReadAllBytes(manifest) : null;
        state = loaded is not null ? JsonSerializer.Deserialize<ConfigManifest>(loaded) ?? throw new IOException("Invalid snapshot manifest") : new(1, new(StringComparer.OrdinalIgnoreCase), []);
        Validate(state); manifestHash=loaded is not null ? Hash(loaded) : null;
    }
    public static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length<3 || !char.IsAsciiLetter(path[0]) || path[1]!=':' || path[2] is not ('\\' or '/') ||
            !System.IO.Path.IsPathFullyQualified(path) || path.IndexOf(':', 2) >= 0 || path.Split('\\', '/').Skip(1).Any(p => p == ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new IOException("Only local absolute paths without traversal, device paths, ambiguous suffixes or ADS are supported.");
        foreach(var part in path[3..].Split('\\','/'))
        {
            var stem=part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length==4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')) throw new IOException("Windows device names are unsupported.");
        }
        return System.IO.Path.GetFullPath(path);
    }
    public static void Safe(string path)
    {
        for (string? p = LocalPath(path); p != null; p = System.IO.Path.GetDirectoryName(p))
        {
            FileAttributes attributes;
            try { attributes=File.GetAttributes(p); }
            catch(FileNotFoundException) { continue; }
            catch(DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse files/ancestors are unsupported: " + p);
        }
    }
    static string Key(string game) => ExecutablePathRules.Normalize(game)?.ToUpperInvariant() ?? throw new IOException("Invalid canonical game identity.");
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static readonly HashSet<string> Extensions = new(".ini .cfg .conf .json .xml .yaml .yml .toml .txt".Split(' '), StringComparer.OrdinalIgnoreCase);
    public static (string Text, string Encoding) Decode(byte[] b)
    {
        Encoding encoding; int skip; string label;
        if (b.Length >= 4 && (b.AsSpan(0,4).SequenceEqual(new byte[]{255,254,0,0}) || b.AsSpan(0,4).SequenceEqual(new byte[]{0,0,254,255}))) throw new IOException("UTF-32 is unsupported.");
        if (b.Length >= 2 && b[0] == 255 && b[1] == 254) { encoding = new UnicodeEncoding(false,true,true); skip=2; label="UTF-16 LE BOM"; }
        else if (b.Length >= 2 && b[0] == 254 && b[1] == 255) { encoding = new UnicodeEncoding(true,true,true); skip=2; label="UTF-16 BE BOM"; }
        else { encoding = new UTF8Encoding(false,true); skip=b.AsSpan().StartsWith(new byte[]{239,187,191}) ? 3 : 0; label=skip==3 ? "UTF-8 BOM" : "UTF-8"; }
        var text=encoding.GetString(b,skip,b.Length-skip);
        if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) throw new IOException("Binary/control characters are unsupported.");
        return (text,label);
    }
    static byte[] Read(string path)
    {
        path=LocalPath(path); Safe(path);
        if (!Extensions.Contains(System.IO.Path.GetExtension(path))) throw new IOException("Unsupported configuration extension: " + path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (stream.Length>MaxBytes) throw new IOException("Configuration exceeds 1 MiB: " + path);
        var bytes=new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.Length!=bytes.Length) throw new IOException("File changed while reading: " + path);
        Decode(bytes); return bytes;
    }
    string Payload(Guid id) { if(id==Guid.Empty) throw new IOException("Invalid payload ID"); var p=System.IO.Path.Combine(root,id.ToString("N")+".bin"); Safe(p); return p; }
    byte[] Bytes(ConfigFile f)
    {
        var p=Payload(f.Payload); using var s=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(s.Length!=f.Size || s.Length>MaxBytes) throw new IOException("Corrupt payload size: " + f.Payload);
        var b=new byte[f.Size]; s.ReadExactly(b);
        if(Hash(b)!=f.Hash || Decode(b).Encoding!=f.Encoding) throw new IOException("Corrupt snapshot payload: " + f.Payload);
        return b;
    }
    void Validate(ConfigManifest m)
    {
        if(m.Version!=1 || m.Selections==null || m.Snapshots==null) throw new IOException("Unsupported/corrupt snapshot manifest; retained for diagnosis.");
        foreach(var pair in m.Selections) { if(Key(pair.Key)!=pair.Key || pair.Value is null || pair.Value.Length>10 || pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=pair.Value.Length) throw new IOException("Invalid selection"); foreach(var p in pair.Value) if(LocalPath(p)!=p || !Extensions.Contains(System.IO.Path.GetExtension(p))) throw new IOException("Invalid path"); }
        var ids=new HashSet<Guid>(); var payloads=new HashSet<Guid>();
        foreach(var s in m.Snapshots)
        {
            if(s is null || s.Id==Guid.Empty || !ids.Add(s.Id) || Key(s.Game)!=s.Game || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length>200 || s.CreatedUtc==default || s.Files is null || s.Files.Length is <1 or >10 || s.Reports==null) throw new IOException("Invalid snapshot metadata");
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var f in s.Files) { if(f is null || !Extensions.Contains(System.IO.Path.GetExtension(f.Path)) || LocalPath(f.Path)!=f.Path || !paths.Add(f.Path) || !payloads.Add(f.Payload) || f.Hash is null || f.Hash.Length!=64 || f.Size<0 || f.Size>MaxBytes) throw new IOException("Invalid file metadata"); Bytes(f); }
            foreach(var p in s.Reports) LocalPath(p);
        }
    }
    void Save(ConfigManifest next)
    {
        Safe(root); Safe(manifest); Safe(manifest+".bak");
        var observed=File.Exists(manifest) ? Hash(File.ReadAllBytes(manifest)) : null; if(observed!=manifestHash) throw new IOException("Snapshot store changed in another window; reopen it before saving.");
        var temp=System.IO.Path.Combine(root,Guid.NewGuid().ToString("N")+".tmp");
        var serialized=JsonSerializer.SerializeToUtf8Bytes(next);
        if(serialized.Length>16*MaxBytes) throw new IOException("Snapshot manifest exceeds supported size.");
        try { using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) stream.Write(serialized); if(File.Exists(manifest)) File.Replace(temp,manifest,manifest+".bak"); else File.Move(temp,manifest); state=next; manifestHash=Hash(serialized); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public string[] Selection(string game) => state.Selections.GetValueOrDefault(Key(game), []).ToArray();
    public ConfigSnapshot[] Snapshots(string game) => state.Snapshots.Where(s=>s.Game==Key(game)).Select(s=>s with { Files=s.Files.ToArray(), Reports=s.Reports.ToArray() }).ToArray();
    public void Select(string game, IEnumerable<string> paths)
    {
        var selected=paths.Select(LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if(selected.Length>10) throw new IOException("Select at most 10 files.");
        foreach(var p in selected) { if(p.StartsWith(root+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("The snapshot store cannot snapshot or restore its own files."); Read(p); }
        var selections=new Dictionary<string,string[]>(state.Selections){[Key(game)]=selected}; Save(state with { Selections=selections });
    }
    public ConfigSnapshot Create(string game,string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Length>200) throw new IOException("Enter a snapshot name (1–200 characters).");
        var paths=Selection(game); if(paths.Length==0) throw new IOException("Explicitly select configuration files first.");
        var files=new List<ConfigFile>();
        var created=new List<string>();
        try
        {
            foreach(var p in paths) { var b=Read(p); var id=Guid.NewGuid(); var payload=Payload(id); using(var s=new FileStream(payload,FileMode.CreateNew)) { created.Add(payload); s.Write(b); } files.Add(new(id,p,Hash(b),b.Length,Decode(b).Encoding)); }
            var snapshot=new ConfigSnapshot(Guid.NewGuid(),Key(game),name,DateTime.UtcNow,files.ToArray(),[]);
            Save(state with { Snapshots=[..state.Snapshots,snapshot] }); return snapshot with { Files=snapshot.Files.ToArray(), Reports=snapshot.Reports.ToArray() };
        }
        catch { foreach(var p in created) { try { Safe(p); File.Delete(p); } catch { } } throw; }
    }
    ConfigSnapshot Find(string game,Guid id) => state.Snapshots.Single(s=>s.Game==Key(game) && s.Id==id);
    ConfigFile FindFile(string game,Guid id,string path) => Find(game,id).Files.Single(f=>string.Equals(f.Path,path,StringComparison.OrdinalIgnoreCase));
    public string Compare(string game,Guid id,string path,Guid? other=null)
    {
        var old=Bytes(FindFile(game,id,path));
        if(other==null && !Selection(game).Contains(path,StringComparer.OrdinalIgnoreCase)) throw new IOException("File is not currently explicitly selected.");
        return path+"\n"+Difference(Decode(old).Text,Decode(other.HasValue ? Bytes(FindFile(game,other.Value,path)) : Read(path)).Text);
    }
    public static string Difference(string before,string after)
    {
        // Linear work and capped rendering; aligned replacement blocks avoid quadratic LCS on repetitive inputs.
        var a=before.Replace("\r\n","\n").Split('\n'); var b=after.Replace("\r\n","\n").Split('\n');
        int prefix=0; while(prefix<Math.Min(a.Length,b.Length) && a[prefix]==b[prefix]) prefix++;
        int suffix=0; while(suffix<Math.Min(a.Length,b.Length)-prefix && a[^(suffix+1)]==b[^(suffix+1)]) suffix++;
        var lines=new List<string>(); int chars=0; bool truncated=false;
        void Add(string line) { if(lines.Count>=500 || chars+line.Length>64000) {truncated=true;return;} lines.Add(line);chars+=line.Length; }
        for(int i=Math.Max(0,prefix-3);i<prefix;i++) Add("  "+a[i]);
        for(int i=prefix;i<a.Length-suffix;i++) Add("- "+a[i]);
        for(int i=prefix;i<b.Length-suffix;i++) Add("+ "+b[i]);
        for(int i=0;i<Math.Min(3,suffix);i++) Add("  "+a[a.Length-suffix+i]);
        return $"Removed {a.Length-prefix-suffix}; added {b.Length-prefix-suffix} (replacement block comparison).\n"+string.Join("\n",lines)+(truncated ? "\n[Output limited; remaining lines omitted]" : "");
    }
    public void Link(string game,Guid id,string report,bool remove=false)
    {
        report=LocalPath(report); var s=Find(game,id);
        if(!remove) ReadReport(report);
        var reports=remove ? s.Reports.Where(p=>!string.Equals(p,report,StringComparison.OrdinalIgnoreCase)).ToArray() : s.Reports.Append(report).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Save(state with { Snapshots=state.Snapshots.Select(x=>x.Id==id ? x with { Reports=reports } : x).ToArray() });
    }
    public static string ReadReport(string report)
    {
        report=LocalPath(report); Safe(report);
        using var stream=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>32*MaxBytes) throw new IOException("Report exceeds 32 MiB.");
        using var reader=new StreamReader(stream,new UTF8Encoding(false,true),true);
        var text=reader.ReadToEnd();
        if(!ReportTimeline.TryParse(text,out _,out var error)) throw new IOException("Select a supported saved performance report: "+error);
        return text;
    }
    public RestorePlan Preview(string game,Guid id,string path)
    {
        var f=FindFile(game,id,path); if(!Selection(game).Contains(path,StringComparer.OrdinalIgnoreCase)) throw new IOException("File is not currently selected."); var current=Read(path); var saved=Bytes(f);
        var difference=path+"\nCurrent file → saved snapshot (minus removes current lines; plus restores snapshot lines).\nEncoding: "+Decode(current).Encoding+" → "+f.Encoding+"\n"+Difference(Decode(current).Text,Decode(saved).Text);
        if(Hash(current)!=f.Hash && Decode(current).Text.Replace("\r\n","\n")==Decode(saved).Text.Replace("\r\n","\n")) difference+="\nText is equal after newline normalization, but file bytes/encoding/line endings differ.";
        return new(id,Key(game),path,Hash(current),f.Hash,difference);
    }
    public string Restore(RestorePlan plan,Func<string?> gameClosedGuard)
    {
        var f=FindFile(plan.Game,plan.Snapshot,plan.Path);
        Safe(manifest);
        if(Hash(File.ReadAllBytes(manifest))!=manifestHash) throw new IOException("Snapshot selections changed in another window; reopen and compare again.");
        if(!Selection(plan.Game).Contains(plan.Path,StringComparer.OrdinalIgnoreCase) || f.Hash!=plan.SnapshotHash) throw new IOException("Restore is out of scope.");
        var bytes=Bytes(f); var current=Read(plan.Path);
        if(Hash(current)!=plan.CurrentHash) throw new IOException("Target changed since preview; compare again.");
        if((File.GetAttributes(plan.Path)&FileAttributes.ReadOnly)!=0) throw new IOException("Target is read-only.");
        var refusal=gameClosedGuard(); if(refusal!=null) throw new IOException(refusal);
        var backup=plan.Path+".mehtools-"+Guid.NewGuid().ToString("N")+".bak";
        var stage=plan.Path+".mehtools-"+Guid.NewGuid().ToString("N")+".tmp"; bool replaced=false, stageCreated=false, backupVerified=false;
        try
        {
            Safe(backup); Safe(stage);
            using(var s=new FileStream(backup,FileMode.CreateNew)) s.Write(current);
            if(Hash(File.ReadAllBytes(backup))!=plan.CurrentHash) throw new IOException("Backup verification failed.");
            backupVerified=true;
            using(var s=new FileStream(stage,FileMode.CreateNew)) { stageCreated=true; s.Write(bytes); }
            refusal=gameClosedGuard(); if(refusal!=null) throw new IOException(refusal);
            Safe(plan.Path); Safe(stage);
            if(Hash(File.ReadAllBytes(stage))!=f.Hash) throw new IOException("Staged payload verification failed.");
            if((File.GetAttributes(plan.Path)&FileAttributes.ReadOnly)!=0) throw new IOException("Target became read-only.");
            if(Hash(Read(plan.Path))!=plan.CurrentHash) throw new IOException("Target changed since preview; compare again.");
            Safe(manifest);
            if(Hash(File.ReadAllBytes(manifest))!=manifestHash) throw new IOException("Snapshot selections changed; reopen and compare again.");
            File.Replace(stage,plan.Path,null); replaced=true;
            if(Hash(Read(plan.Path))!=f.Hash) throw new IOException("Final verification failed; do not blindly roll back over concurrent edits.");
            return "Restored and verified. Backup: "+backup;
        }
        catch(Exception ex) { throw new IOException($"Replacement {(replaced ? "happened" : "did not happen")}. {(backupVerified ? "Verified backup" : "Backup could not be verified (attempted path)")}: {backup}. {ex.Message}",ex); }
        finally { if(stageCreated) { try { Safe(stage); File.Delete(stage); } catch { } } }
    }
    public static string? ClosedGuard(GameRule game,IReadOnlyList<RunningProcess> processes)
        => !new[]{"path","name","folder"}.Contains(game.MatchMode) || ExecutablePathRules.Normalize(game.ExecutablePath)==null ||
            (game.MatchMode=="folder" && processes.Any(p=>p.FullPath is null)) ||
            AutomationPolicy.EvaluateRule(game,processes).Processes.Any(p=>p.MatchKind!=null || p.Process.FullPath==null) ? "Game active or a possible matching process is inaccessible. Close the game and compare again." : null;
}
