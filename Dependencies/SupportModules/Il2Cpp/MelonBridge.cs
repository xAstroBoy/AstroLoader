using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MelonLoader.Support
{
    /// <summary>
    /// MelonBridge - a UnityExplorer-class remote inspector/controller BUILT INTO MelonLoader itself (this is
    /// the Il2Cpp support module, not a mod). It exposes the full runtime power of MelonLoader over TCP so the
    /// live game can be inspected/controlled from a PC client (and the same engine drives an on-headset view):
    ///   - scene enumeration + activation/loading
    ///   - GameObject tree (scene roots, DontDestroyOnLoad, children, global name search)
    ///   - every MonoBehaviour/Component: list + get/set fields&amp;properties, invoke methods
    ///   - runtime Harmony patching (log or block any method) via Il2CppInterop HarmonySupport
    ///
    /// All Il2Cpp work runs on the MAIN thread: a bg thread only moves strings; commands are queued and drained
    /// from <see cref="Pump"/> (called every frame by SM_Component.Update). Each response ends with "&lt;&lt;END&gt;&gt;".
    /// Connect:  adb forward tcp:28000 tcp:28000   then send newline commands to 127.0.0.1:28000.
    /// </summary>
    internal static class MelonBridge
    {
        private const int Port = 28000;
        internal const string Version = "1.0.0";

        private sealed class Req
        {
            public string Cmd;
            public string Resp;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<Req> Queue = new ConcurrentQueue<Req>();
        private static volatile bool _run;
        private static Thread _thread;

        // ---- harmony hook registry + hit log (shared with bg via locks) ----
        private static readonly Dictionary<string, HookEntry> _hooks = new Dictionary<string, HookEntry>();
        private static readonly Queue<string> _hookLog = new Queue<string>();
        private static HarmonyLib.Harmony _harmony;

        private sealed class HookEntry { public MethodBase Method; public bool Block; }

        internal static void Start()
        {
            if (_run) return;
            _run = true;
            try { _harmony = new HarmonyLib.Harmony("MelonBridge"); } catch { }
            _thread = new Thread(ServerLoop) { IsBackground = true, Name = "MelonBridge" };
            _thread.Start();
            MelonLogger.Msg(System.Drawing.Color.Orange,
                "[MelonBridge] v" + Version + " EMBEDDED inspector on tcp:" + Port
                + "  (adb forward tcp:" + Port + " tcp:" + Port + ")");
        }

        internal static void Stop() => _run = false;

        // ---- main thread: drain + execute Il2Cpp work ----
        internal static void Pump()
        {
            int budget = 8;
            while (budget-- > 0 && Queue.TryDequeue(out var r))
            {
                try { r.Resp = Process(r.Cmd); }
                catch (Exception e) { r.Resp = "ERR " + e.Message + "\n<<END>>\n"; }
                r.Done.Set();
            }
        }

        // ========================= command dispatch =========================

        private static string Process(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "<<END>>\n";
            var parts = cmd.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            switch (parts[0].ToLowerInvariant())
            {
                case "help":
                    sb.Append("scenes | scene-activate <i> | scene-load <name|i> | roots [sceneIdx] | ddol | find <substr>\n");
                    sb.Append("children <id> | comps <id> | fields <id> <Type|#i> | methods <id> <Type|#i>\n");
                    sb.Append("members <id> <Type|#i> | get/set <id> <Type|#i> <member> [val] | call <id> <Type|#i> <method> [args...]\n");
                    sb.Append("enabled <id> <Type|#i> <0|1> | destroycomp <id> <Type|#i>\n");
                    sb.Append("toggle <id> [on|off] | setname <id> <name> | destroy <id> | instantiate <id> [parentId]\n");
                    sb.Append("tpos/twpos/trot/tscale <id> x,y,z | setparent <id> <parentId|0> | addcomp <id> <Type>\n");
                    sb.Append("hook <id> <Type|#i> <method> [block] | unhook <key> | hooks | hooklog\n");
                    break;

                case "scenes":
                {
                    sb.Append("active\t").Append(SceneManager.GetActiveScene().name).Append('\n');
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        sb.Append(i).Append('\t').Append(s.name).Append('\t')
                          .Append(s.isLoaded ? "loaded" : "loading").Append('\t')
                          .Append("build=").Append(s.buildIndex).Append('\t')
                          .Append("roots=").Append(s.rootCount).Append('\n');
                    }
                    break;
                }

                case "scene-activate":
                {
                    int i = int.Parse(parts[1]);
                    var s = SceneManager.GetSceneAt(i);
                    SceneManager.SetActiveScene(s);
                    sb.Append("ok active = ").Append(s.name).Append('\n');
                    break;
                }

                case "scene-load":
                {
                    if (int.TryParse(parts[1], out int bi)) SceneManager.LoadScene(bi);
                    else SceneManager.LoadScene(parts[1]);
                    sb.Append("ok loading ").Append(parts[1]).Append('\n');
                    break;
                }

                case "roots":
                {
                    Scene scene = parts.Length > 1 ? SceneManager.GetSceneAt(int.Parse(parts[1])) : SceneManager.GetActiveScene();
                    foreach (var go in scene.GetRootGameObjects()) AppendGoRow(go, sb);
                    break;
                }

                case "ddol":
                {
                    // The standard UnityExplorer trick: a DontDestroyOnLoad object reveals the hidden DDOL scene.
                    var probe = new GameObject("__melonbridge_probe__");
                    UnityEngine.Object.DontDestroyOnLoad(probe);
                    var scene = probe.scene;
                    foreach (var go in scene.GetRootGameObjects())
                        if (go != null && go.name != "__melonbridge_probe__") AppendGoRow(go, sb);
                    UnityEngine.Object.Destroy(probe);
                    break;
                }

                case "find":
                {
                    if (parts.Length < 2) { sb.Append("ERR usage: find <substr>\n"); break; }
                    string needle = cmd.Substring(cmd.IndexOf(parts[1], StringComparison.Ordinal)).Trim().ToLowerInvariant();
                    int n = 0;
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>()))
                    {
                        var g = o.TryCast<GameObject>();
                        if (g == null || g.name == null || !g.name.ToLowerInvariant().Contains(needle)) continue;
                        AppendGoRow(g, sb);
                        n++;   // NO CAP - return the full result set
                    }
                    break;
                }

                case "route":
                {
                    // Resolve ANY instance id so the client can open the right tab kind when drilling into a
                    // member's object reference. Returns: "go\t<id>\t<name>" | "comp\t<goId>\t<goName>\t<idx>\t<Type>" | "obj\t<Type>" | "none"
                    int rid = int.Parse(parts[1]);
                    var go = FindGo(rid);
                    if (go != null) { sb.Append("go\t").Append(go.GetInstanceID()).Append('\t').Append(go.name).Append('\n'); break; }
                    UnityEngine.Object obj = null;
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Component>()))
                    {
                        var u = o.TryCast<UnityEngine.Object>();
                        if (u != null && u.GetInstanceID() == rid) { obj = u; break; }
                    }
                    if (obj == null) { sb.Append("none\n"); break; }
                    var comp = obj.TryCast<Component>();
                    if (comp != null)
                    {
                        var owner = comp.gameObject;
                        var comps = owner.GetComponents(Il2CppType.Of<Component>());
                        int idx = -1;
                        for (int i = 0; i < comps.Length; i++) if (comps[i] != null && comps[i].GetInstanceID() == rid) { idx = i; break; }
                        sb.Append("comp\t").Append(owner.GetInstanceID()).Append('\t').Append(owner.name).Append('\t')
                          .Append(idx).Append('\t').Append(RealType(comp).FullName).Append('\n');
                        break;
                    }
                    sb.Append("obj\t").Append(RealType(obj).FullName).Append('\n');
                    break;
                }

                case "exists":
                {
                    // Cheap liveness check so the client can auto-close tabs whose target was destroyed.
                    sb.Append(FindGo(int.Parse(parts[1])) != null ? "yes" : "no").Append('\n');
                    break;
                }

                case "types":
                {
                    // Type-name autocomplete (UnityExplorer TypeCompleter): suggest type names containing <substr>.
                    EnsureTypeNames();
                    string q = parts.Length > 1 ? cmd.Substring(cmd.IndexOf(parts[1], StringComparison.Ordinal)).Trim() : "";
                    int n = 0;
                    // exact/prefix matches first, then contains
                    foreach (var nm in _typeNames)
                        if (q.Length == 0 || nm.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                        { sb.Append(nm).Append('\n'); if (++n >= 40) break; }
                    if (n < 40 && q.Length > 0)
                        foreach (var nm in _typeNames)
                            if (!nm.StartsWith(q, StringComparison.OrdinalIgnoreCase) && nm.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                            { sb.Append(nm).Append('\n'); if (++n >= 40) break; }
                    break;
                }

                case "findtype":
                {
                    // Object Search by TYPE (UnityExplorer class search): all active objects of a component type,
                    // mapped to their GameObjects. Great for locating the rendered object (e.g. SkinnedMeshRenderer)
                    // when the named node is just a logic/spawn container.
                    if (parts.Length < 2) { sb.Append("ERR usage: findtype <TypeName>\n"); break; }
                    Type t = FindAnyType(parts[1]);
                    if (t == null) { sb.Append("ERR type '").Append(parts[1]).Append("' not found\n"); break; }
                    // FindObjectsOfTypeAll = ALL loaded scenes + DontDestroyOnLoad + INACTIVE (same scope as `find`),
                    // not just the active scene's active objects.
                    var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
                    var seen = new HashSet<int>();
                    int n = 0;
                    foreach (var o in arr)
                    {
                        GameObject g = o.TryCast<GameObject>();
                        if (g == null) { var c = o.TryCast<Component>(); if (c != null) g = c.gameObject; }
                        if (g == null || !seen.Add(g.GetInstanceID())) continue;
                        AppendGoRow(g, sb);
                        n++;   // NO CAP - return the full result set
                    }
                    break;
                }

                case "children":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var tr = go.transform;
                    int cc = tr.childCount;   // snapshot; subscene streaming can change childCount mid-iterate
                    for (int i = 0; i < cc; i++)
                    {
                        try { var ch = tr.GetChild(i); if (ch != null) AppendGoRow(ch.gameObject, sb); }
                        catch { break; }   // count shrank under us - stop
                    }
                    break;
                }

                case "parent":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var p = go.transform.parent;
                    if (p == null) sb.Append("none (scene root)\n");
                    else AppendGoRow(p.gameObject, sb);
                    break;
                }

                case "path":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var stack = new List<string>();
                    var tr = go.transform;
                    while (tr != null) { stack.Add(tr.name); tr = tr.parent; }
                    stack.Reverse();
                    sb.Append(string.Join("/", stack)).Append("\t(scene: ").Append(go.scene.name).Append(")\n");
                    break;
                }

                case "where":
                {
                    // Classify an object: asset/prefab (not in a scene) vs a named scene vs DontDestroyOnLoad.
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var sc = go.scene;
                    bool valid; string nm;
                    try { valid = sc.IsValid(); } catch { valid = false; }
                    try { nm = sc.name ?? ""; } catch { nm = ""; }
                    string kind = !valid ? "asset" : (nm == "DontDestroyOnLoad" ? "ddol" : "scene");
                    sb.Append(kind).Append('\t').Append(string.IsNullOrEmpty(nm) ? "(none)" : nm).Append('\n');
                    break;
                }

                case "setddol":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    bool on = parts.Length > 2 && (parts[2] == "1" || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("on", StringComparison.OrdinalIgnoreCase));
                    try
                    {
                        if (on) { UnityEngine.Object.DontDestroyOnLoad(go); sb.Append("ok marked DontDestroyOnLoad\n"); }
                        else { SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene()); sb.Append("ok moved to active scene (no longer DDOL; root objects only)\n"); }
                    }
                    catch (Exception e) { sb.Append("ERR ").Append(e.Message).Append('\n'); }
                    break;
                }

                case "comps":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var comps = go.GetComponents(Il2CppType.Of<Component>());
                    // row: "#i \t FullTypeName \t on|off|-"  (enabled state via the 'enabled' bool property -
                    // covers Behaviour, Renderer, Collider, etc.; '-' = no such toggle, e.g. Transform/MeshFilter)
                    for (int i = 0; i < comps.Length; i++)
                    {
                        sb.Append('#').Append(i).Append('\t');
                        if (comps[i] == null) { sb.Append("<null>\t-\n"); continue; }
                        var rt = RealType(comps[i]);
                        sb.Append(rt.FullName).Append('\t').Append(ReadEnabled(comps[i], rt)).Append('\n');
                    }
                    break;
                }

                case "fields":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    DumpMembers(c, rt, sb);
                    break;
                }

                case "methods":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    DumpMethods(c, rt, sb);
                    break;
                }

                case "members":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    DumpMembersTyped(c, rt, sb);
                    break;
                }

                case "enum":
                {
                    // Enumerate a collection/array/list member's elements: enum <id> <Type|#i> <member> [start] [count]
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    if (parts.Length < 4) { sb.Append("ERR usage: enum <id> <Type|#i> <member> [start] [count]\n"); break; }
                    object coll = GetMemberValue(c, rt, parts[3]);
                    if (coll == null) { sb.Append("null\n"); break; }
                    int start = parts.Length > 4 ? int.Parse(parts[4]) : 0;
                    int count = parts.Length > 5 ? int.Parse(parts[5]) : int.MaxValue;   // NO CAP by default
                    EnumerateCollection(coll, start, count, sb);
                    break;
                }

                case "enabled":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    if (parts.Length < 4) { sb.Append("ERR usage: enabled <id> <Type> <0|1>\n"); break; }
                    bool on = parts[3] == "1" || parts[3].Equals("true", StringComparison.OrdinalIgnoreCase) || parts[3].Equals("on", StringComparison.OrdinalIgnoreCase);
                    var ep = rt.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (ep == null || ep.PropertyType != typeof(bool) || !ep.CanWrite) { sb.Append("ERR component has no settable 'enabled'\n"); break; }
                    ep.SetValue(c, on);
                    sb.Append("ok enabled=").Append(ep.GetValue(c)).Append('\n');
                    break;
                }

                case "destroycomp":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    string nm = rt.Name;
                    UnityEngine.Object.Destroy(c as UnityEngine.Object);
                    sb.Append("ok destroyed component ").Append(nm).Append('\n');
                    break;
                }

                case "get":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    if (parts.Length < 4) { sb.Append("ERR usage: get <id> <Type> <member>\n"); break; }
                    sb.Append(GetMember(c, rt, parts[3])).Append('\n');
                    break;
                }

                case "set":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    if (parts.Length < 5) { sb.Append("ERR usage: set <id> <Type> <member> <value>\n"); break; }
                    int memberAt = cmd.IndexOf(parts[3], cmd.IndexOf(parts[2], StringComparison.Ordinal), StringComparison.Ordinal);
                    string after = cmd.Substring(memberAt + parts[3].Length).Trim();
                    sb.Append(SetMember(c, rt, parts[3], after)).Append('\n');
                    break;
                }

                case "call":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    if (parts.Length < 4) { sb.Append("ERR usage: call <id> <Type> <method> [args...]\n"); break; }
                    var args = new string[parts.Length - 4];
                    Array.Copy(parts, 4, args, 0, args.Length);
                    sb.Append(CallMethod(c, rt, parts[3], args)).Append('\n');
                    break;
                }

                case "toggle":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    bool on = parts.Length > 2 ? (parts[2] == "1" || parts[2].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase)) : !go.activeSelf;
                    go.SetActive(on);
                    sb.Append("ok ").Append(go.name).Append(" active=").Append(go.activeSelf).Append('\n');
                    break;
                }

                case "setname":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    go.name = cmd.Substring(cmd.IndexOf(parts[2], cmd.IndexOf(parts[1], StringComparison.Ordinal) + parts[1].Length, StringComparison.Ordinal)).Trim();
                    sb.Append("ok renamed -> ").Append(go.name).Append('\n');
                    break;
                }

                case "destroy":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    string nm = go.name;
                    UnityEngine.Object.Destroy(go);
                    sb.Append("ok destroyed ").Append(nm).Append('\n');
                    break;
                }

                case "instantiate":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var clone = UnityEngine.Object.Instantiate(go).TryCast<GameObject>();
                    if (clone == null) { sb.Append("ERR clone failed\n"); break; }
                    if (parts.Length > 2)
                    {
                        var parent = FindGo(int.Parse(parts[2]));
                        if (parent != null) clone.transform.SetParent(parent.transform, false);
                    }
                    sb.Append("ok cloned -> "); AppendGoRow(clone, sb);
                    break;
                }

                case "newchild":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    string nm = parts.Length > 2
                        ? cmd.Substring(cmd.IndexOf(parts[2], cmd.IndexOf(parts[1], StringComparison.Ordinal) + parts[1].Length, StringComparison.Ordinal)).Trim()
                        : "GameObject";
                    var child = new GameObject(nm);
                    child.transform.SetParent(go.transform, false);
                    sb.Append("ok created -> "); AppendGoRow(child, sb);
                    break;
                }

                case "setparent":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    int pid = int.Parse(parts[2]);
                    go.transform.SetParent(pid == 0 ? null : FindGo(pid)?.transform, true);
                    sb.Append("ok reparented ").Append(go.name).Append('\n');
                    break;
                }

                case "tpos": case "twpos": case "trot": case "tscale":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var f = Floats(parts[2], 3);
                    var tr = go.transform;
                    string prop = parts[0].ToLowerInvariant() switch
                    {
                        "tpos" => "localPosition", "twpos" => "position", "trot" => "localEulerAngles", "tscale" => "localScale", _ => "localPosition"
                    };
                    // native struct write (the managed setter truncates the Vector3 to its X component)
                    var ob = (Il2CppObjectBase)tr;
                    bool wrote = WriteStructProp(ob.Pointer, "set_" + prop, new float[] { f[0], f[1], f[2] });
                    if (!wrote)   // fallback (may truncate)
                    {
                        var v = new Vector3(f[0], f[1], f[2]);
                        switch (prop) { case "localPosition": tr.localPosition = v; break; case "position": tr.position = v; break; case "localEulerAngles": tr.localEulerAngles = v; break; case "localScale": tr.localScale = v; break; }
                    }
                    sb.Append("ok ").Append(parts[0]).Append(" = ").Append(StructValueOrNull(tr, prop, typeof(Vector3)) ?? "set").Append('\n');
                    break;
                }

                case "addcomp":
                {
                    var go = FindGo(int.Parse(parts[1]));
                    if (go == null) { sb.Append("ERR not found\n"); break; }
                    var type = ResolveIl2CppType(parts[2]);
                    if (type == null) { sb.Append("ERR type '").Append(parts[2]).Append("' not found\n"); break; }
                    var comp = go.AddComponent(type);
                    sb.Append(comp == null ? "ERR addcomponent failed" : "ok added " + comp.GetType().FullName).Append('\n');
                    break;
                }

                case "hook":
                {
                    var c = ResolveComp(parts, out Type rt, out string err);
                    if (c == null) { sb.Append("ERR ").Append(err).Append('\n'); break; }
                    if (parts.Length < 4) { sb.Append("ERR usage: hook <id> <Type> <method> [block]\n"); break; }
                    bool block = parts.Length > 4 && parts[4].Equals("block", StringComparison.OrdinalIgnoreCase);
                    sb.Append(AddHook(rt, parts[3], block)).Append('\n');
                    break;
                }

                case "enumopts":
                {
                    // Names of an enum type, for the inspector's enum dropdown editor.
                    if (parts.Length < 2) { sb.Append("ERR usage: enumopts <EnumTypeName>\n"); break; }
                    Type et = FindAnyType(parts[1]);
                    if (et == null) { sb.Append("ERR type not found\n"); break; }
                    var u = Nullable.GetUnderlyingType(et) ?? et;
                    if (!u.IsEnum) { sb.Append("ERR not an enum\n"); break; }
                    foreach (var n in Enum.GetNames(u)) sb.Append(n).Append('\n');
                    break;
                }

                case "timescale":
                {
                    if (parts.Length > 1) { Time.timeScale = float.Parse(parts[1], CultureInfo.InvariantCulture); sb.Append("ok timeScale=").Append(Time.timeScale.ToString(CultureInfo.InvariantCulture)).Append('\n'); }
                    else sb.Append("timeScale=").Append(Time.timeScale.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                }

                case "cam":
                {
                    var cam = GetCam();
                    if (cam == null) { sb.Append("none\n"); break; }
                    var go = cam.gameObject; var trp = ((Il2CppObjectBase)cam.transform).Pointer;
                    float[] p = new float[3], e = new float[3];
                    ReadStructFloats(trp, "get_position", "position", 3, p);       // NATIVE - managed .position is corrupt
                    ReadStructFloats(trp, "get_eulerAngles", "eulerAngles", 3, e);
                    sb.Append("camera\t").Append(go.name).Append('\t').Append(go.GetInstanceID()).Append('\n');
                    sb.Append("position\t").Append(Vec3(p)).Append('\n');
                    sb.Append("eulerAngles\t").Append(Vec3(e)).Append('\n');
                    sb.Append("fieldOfView\t").Append(cam.fieldOfView.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                }

                case "camnudge":   // move camera relative to its own orientation: right/up/forward * (x,y,z)
                {
                    var cam = GetCam(); if (cam == null) { sb.Append("ERR no camera\n"); break; }
                    var f = Floats(parts[1], 3); var trp = ((Il2CppObjectBase)cam.transform).Pointer;
                    float[] pos = new float[3], right = new float[3], up = new float[3], fwd = new float[3];
                    ReadStructFloats(trp, "get_position", "position", 3, pos);
                    ReadStructFloats(trp, "get_right", "right", 3, right);
                    ReadStructFloats(trp, "get_up", "up", 3, up);
                    ReadStructFloats(trp, "get_forward", "forward", 3, fwd);
                    var np = new float[3];
                    for (int i = 0; i < 3; i++) np[i] = pos[i] + right[i] * f[0] + up[i] * f[1] + fwd[i] * f[2];
                    WriteStructProp(trp, "set_position", np);                       // NATIVE write
                    sb.Append("ok pos=").Append(Vec3(np)).Append('\n');
                    break;
                }

                case "overlap":
                {
                    // Native Physics.OverlapSphere at the PLAYER (camera/head) position -> colliders' GameObjects.
                    // Lets us find what you're standing in (e.g. the laser barrier you're being zapped by).
                    float radius = parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 2.5f;
                    var cam = GetCam(); if (cam == null) { sb.Append("ERR no camera\n"); break; }
                    float[] pos = new float[3];
                    if (!ReadStructFloats(((Il2CppObjectBase)cam.transform).Pointer, "get_position", "position", 3, pos)) { sb.Append("ERR no cam pos\n"); break; }
                    sb.Append("origin\t").Append(Vec3(pos)).Append("\tr=").Append(radius.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    OverlapAt(pos, radius, sb);
                    break;
                }

                case "camrot":     // rotate camera by local euler delta (x,y,z)
                {
                    var cam = GetCam(); if (cam == null) { sb.Append("ERR no camera\n"); break; }
                    var f = Floats(parts[1], 3);
                    cam.transform.Rotate(f[0], f[1], f[2]);
                    sb.Append("ok rot=").Append(StripParen(cam.transform.eulerAngles.ToString())).Append('\n');
                    break;
                }

                case "camfov":
                {
                    var cam = GetCam(); if (cam == null) { sb.Append("ERR no camera\n"); break; }
                    if (parts.Length > 1) cam.fieldOfView = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    sb.Append("ok fov=").Append(cam.fieldOfView.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                }

                case "koallgoons":
                {
                    // Quick KO test: find every ACTIVE AIGoonPawn instance and call its InstantKO().
                    Type proxy = FindProxyType("AIGoonPawn");
                    if (proxy == null) { sb.Append("ERR AIGoonPawn proxy type not found\n"); break; }
                    var ko = proxy.GetMethod("InstantKO", BindingFlags.Public | BindingFlags.Instance);
                    if (ko == null) { sb.Append("ERR InstantKO() not found on ").Append(proxy.FullName).Append('\n'); break; }
                    var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(proxy));
                    var tryCast = typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(proxy);
                    int n = 0, total = all.Length;
                    foreach (var o in all)
                    {
                        try { ko.Invoke(tryCast.Invoke(o, null), null); n++; }
                        catch (Exception e) { sb.Append("  KO fail: ").Append(e.InnerException?.Message ?? e.Message).Append('\n'); }
                    }
                    sb.Append("ok InstantKO called on ").Append(n).Append('/').Append(total).Append(" active AIGoonPawn\n");
                    break;
                }

                case "eval":
                {
                    // C# console: code arrives base64-encoded (carries newlines/spaces over the line protocol).
                    if (parts.Length < 2) { sb.Append("ERR usage: eval <base64-code>\n"); break; }
                    string code;
                    try { code = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); }
                    catch { sb.Append("ERR bad base64\n"); break; }
                    sb.Append(EvalCSharp(code));
                    break;
                }

                case "typemethods":
                {
                    // List all methods of a class by NAME (no instance needed) so the Hook Manager can inspect
                    // any class, runtime-instantiated or not. row: name \t ret \t (sig) \t static|instance \t declType
                    if (parts.Length < 2) { sb.Append("ERR usage: typemethods <TypeName>\n"); break; }
                    Type t = FindAnyType(parts[1]);
                    if (t == null) { sb.Append("ERR type '").Append(parts[1]).Append("' not found\n"); break; }
                    DumpMethodsOfType(t, sb);
                    break;
                }

                case "typemembers":
                {
                    // Inspect a class's static fields/properties by NAME (no instance).
                    if (parts.Length < 2) { sb.Append("ERR usage: typemembers <TypeName>\n"); break; }
                    Type t = FindAnyType(parts[1]);
                    if (t == null) { sb.Append("ERR type '").Append(parts[1]).Append("' not found\n"); break; }
                    DumpStaticMembersTyped(t, sb);
                    break;
                }

                case "hooktype":
                {
                    // Hook a method by CLASS NAME + method (static or instance), no live instance required.
                    if (parts.Length < 3) { sb.Append("ERR usage: hooktype <TypeName> <method> [block]\n"); break; }
                    Type t = FindAnyType(parts[1]);
                    if (t == null) { sb.Append("ERR type '").Append(parts[1]).Append("' not found\n"); break; }
                    bool block = parts.Length > 3 && parts[3].Equals("block", StringComparison.OrdinalIgnoreCase);
                    sb.Append(AddHook(t, parts[2], block)).Append('\n');
                    break;
                }

                case "unhook":
                    sb.Append(parts.Length < 2 ? "ERR usage: unhook <key>" : RemoveHook(parts[1])).Append('\n');
                    break;

                case "hooks":
                    lock (_hooks)
                        foreach (var kv in _hooks)
                            sb.Append(kv.Key).Append('\t').Append(kv.Value.Block ? "block" : "log").Append('\n');
                    break;

                case "hooklog":
                    lock (_hookLog) { while (_hookLog.Count > 0) sb.Append(_hookLog.Dequeue()).Append('\n'); }
                    break;

                default:
                    sb.Append("ERR unknown - try 'help'\n");
                    break;
            }
            sb.Append("<<END>>\n");
            return sb.ToString();
        }

        // ========================= object / component resolution =========================

        private static void AppendGoRow(GameObject go, StringBuilder sb)
        {
            if (go == null) return;
            sb.Append(go.GetInstanceID()).Append('\t').Append(go.name).Append('\t')
              .Append(go.activeSelf ? "on" : "off").Append('\t')
              .Append(go.transform.childCount).Append('\t')
              .Append(go.GetComponents(Il2CppType.Of<Component>()).Length).Append('\n');
        }

        private static Camera GetCam()
        {
            var c = Camera.main;
            if (c != null) return c;
            var all = Camera.allCameras;
            if (all != null && all.Length > 0) return all[0];
            return null;
        }

        private static string StripParen(string s)
        {
            var b = new StringBuilder();
            foreach (char c in s) if (c != '(' && c != ')' && c != ' ') b.Append(c);
            return b.ToString();
        }

        private static string Vec3(float[] f) =>
            f[0].ToString("0.###", CultureInfo.InvariantCulture) + "," + f[1].ToString("0.###", CultureInfo.InvariantCulture) + "," + f[2].ToString("0.###", CultureInfo.InvariantCulture);

        // Native UnityEngine.Physics.OverlapSphere(Vector3,float): the Vector3 arg is passed as a raw float[3]
        // pointer (managed marshalling corrupts it). Returns the colliders' GameObjects as rows + the collider type.
        private static unsafe int OverlapAt(float[] pos, float radius, StringBuilder sb)
        {
            IntPtr cls = IL2CPP.GetIl2CppClass("UnityEngine.PhysicsModule.dll", "UnityEngine", "Physics");
            if (cls == IntPtr.Zero) { sb.Append("ERR Physics class not found\n"); return 0; }
            IntPtr m = IL2CPP.il2cpp_class_get_method_from_name(cls, "OverlapSphere", 2);
            if (m == IntPtr.Zero) { sb.Append("ERR OverlapSphere(2) not found\n"); return 0; }
            IntPtr res; IntPtr exc = IntPtr.Zero;
            fixed (float* pp = pos) { float r = radius; void** argv = stackalloc void*[2]; argv[0] = pp; argv[1] = &r; res = IL2CPP.il2cpp_runtime_invoke(m, IntPtr.Zero, argv, ref exc); }
            if (exc != IntPtr.Zero || res == IntPtr.Zero) { sb.Append("ERR OverlapSphere invoke failed\n"); return 0; }
            int len = (int)IL2CPP.il2cpp_array_length(res);
            var seen = new HashSet<int>();
            int n = 0;
            for (int i = 0; i < len; i++)
            {
                IntPtr col = Marshal.ReadIntPtr(res, 0x20 + i * IntPtr.Size);   // il2cpp array data starts at 0x20
                if (col == IntPtr.Zero) continue;
                IntPtr colCls = IL2CPP.il2cpp_object_get_class(col);
                string colType = ""; try { colType = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(colCls)) ?? ""; } catch { }
                IntPtr gm = IL2CPP.il2cpp_class_get_method_from_name(colCls, "get_gameObject", 0);
                if (gm == IntPtr.Zero) continue;
                IntPtr e2 = IntPtr.Zero;
                IntPtr goPtr = IL2CPP.il2cpp_runtime_invoke(gm, col, (void**)null, ref e2);
                if (goPtr == IntPtr.Zero || e2 != IntPtr.Zero) continue;
                GameObject go; try { go = new GameObject(goPtr); } catch { continue; }
                if (go == null || !seen.Add(go.GetInstanceID())) continue;
                sb.Append('[').Append(colType).Append("]\t"); AppendGoRow(go, sb);
                n++;
            }
            sb.Append("(").Append(n).Append(" colliders)\n");
            return n;
        }

        // LRU-ish cache so the hot path (client auto-update re-querying the SAME selected object ~2x/sec) does NOT
        // re-scan every GameObject in memory each time — that full FindObjectsOfTypeAll scan on the game's main
        // thread was lagging the game hard. Cache the last few resolved ids; validate liveness before reuse.
        private static readonly Dictionary<int, GameObject> _goCache = new Dictionary<int, GameObject>();
        // NEGATIVE cache: a destroyed id that the client keeps polling (a dead tab) would otherwise full-scan ALL
        // objects every call (~100k objects in a big streaming scene = multi-second hitch) and LAG hard. Unity
        // instance IDs are NEVER reused, so a miss is PERMANENT - cache it forever (one scan, then instant null).
        private static readonly HashSet<int> _missCache = new HashSet<int>();
        private static GameObject FindGo(int id)
        {
            if (_missCache.Contains(id)) return null;   // permanently gone - never re-scan
            if (_goCache.TryGetValue(id, out var cached))
            {
                try { if (cached != null && cached.GetInstanceID() == id) return cached; }
                catch { }
                _goCache.Remove(id);   // stale/destroyed
            }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>()))
            {
                var g = o.TryCast<GameObject>();
                if (g != null && g.GetInstanceID() == id)
                {
                    if (_goCache.Count > 64) _goCache.Clear();
                    _goCache[id] = g;
                    return g;
                }
            }
            if (_missCache.Count > 16384) _missCache.Clear();
            _missCache.Add(id);   // not found - negative-cache forever (instance ids never recycle)
            return null;
        }

        // Resolve a component selector to the LIVE proxy cast to its REAL il2cpp type (so reflection sees the
        // real members, not just UnityEngine.Component). Returns the casted instance + its managed proxy type.
        private static object ResolveComp(string[] parts, out Type realType, out string err)
        {
            realType = null; err = null;
            if (parts.Length < 3) { err = "usage: <cmd> <id> <Type|#index>"; return null; }
            var go = FindGo(int.Parse(parts[1]));
            if (go == null) { err = "gameobject not found"; return null; }
            var comps = go.GetComponents(Il2CppType.Of<Component>());
            string sel = parts[2];
            Component target = null;
            if (sel.StartsWith("#"))
            {
                int idx = int.Parse(sel.Substring(1));
                if (idx < 0 || idx >= comps.Length) { err = "comp index out of range"; return null; }
                target = comps[idx];
            }
            else
            {
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var rt = RealType(c);
                    if (rt.FullName == sel || rt.Name == sel) { target = c; realType = rt; break; }
                }
                if (target == null) { err = "component '" + sel + "' not found on object"; return null; }
            }
            if (target == null) { err = "component is null"; return null; }
            if (realType == null) realType = RealType(target);
            return CastTo(target, realType);
        }

        // Yoinked from UniverseLib Il2CppReflection.GetActualType: get the real runtime il2cpp type of an object
        // (GetComponents returns base-typed Component proxies, so obj.GetType() is just UnityEngine.Component).
        private static Type RealType(Il2CppObjectBase obj)
        {
            try
            {
                var cppType = Il2CppType.TypeFromPointer(IL2CPP.il2cpp_object_get_class(obj.Pointer));
                string fn = cppType.FullName;
                if (!string.IsNullOrEmpty(fn))
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = asm.GetType(fn, false); } catch { }
                        if (t != null && typeof(Il2CppObjectBase).IsAssignableFrom(t)) return t;
                    }
                }
            }
            catch { }
            return obj.GetType();
        }

        // Il2CppObjectBase.TryCast<T>() over a runtime Type, so reflection GetValue/Invoke target the real type.
        private static object CastTo(Il2CppObjectBase obj, Type t)
        {
            try { return typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(t).Invoke(obj, null) ?? obj; }
            catch { return obj; }
        }

        // Read a component's 'enabled' bool (Behaviour/Renderer/Collider all expose it) for the comps list.
        // Returns "on"/"off"/"-" ('-' = no such toggle, e.g. Transform, MeshFilter).
        private static string ReadEnabled(Il2CppObjectBase comp, Type rt)
        {
            try
            {
                var ep = rt.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (ep == null || ep.PropertyType != typeof(bool) || !ep.CanRead) return "-";
                return ((bool)ep.GetValue(CastTo(comp, rt))) ? "on" : "off";
            }
            catch { return "-"; }
        }

        // ===== correct struct (Vector/Quaternion/Color) reads =====
        // System.Reflection.Invoke truncates Il2Cpp value-type returns (a Vector3 getter comes back as (x,0,0) -
        // only the first 4 bytes survive). Read the real bytes via il2cpp native invoke+unbox (properties) or
        // il2cpp_field_get_value (fields). Returns the canonical "(x, y, z[, w])" string, or null if not handled.
        private static unsafe bool ReadStructFloats(IntPtr objPtr, string getter, string fieldName, int count, float[] outF)
        {
            if (objPtr == IntPtr.Zero) return false;
            IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
            if (klass == IntPtr.Zero) return false;
            // property getter (il2cpp_class_get_method_from_name walks base classes)
            if (getter != null)
            {
                IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, getter, 0);
                if (method != IntPtr.Zero)
                {
                    IntPtr exc = IntPtr.Zero;
                    IntPtr res = IL2CPP.il2cpp_runtime_invoke(method, objPtr, (void**)null, ref exc);
                    if (exc == IntPtr.Zero && res != IntPtr.Zero)
                    {
                        IntPtr data = IL2CPP.il2cpp_object_unbox(res);
                        if (data != IntPtr.Zero) { Marshal.Copy(data, outF, 0, count); return true; }
                    }
                }
            }
            // field (walk the class hierarchy for the field)
            if (fieldName != null)
            {
                for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
                {
                    IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(k, fieldName);
                    if (field != IntPtr.Zero)
                    {
                        fixed (float* pf = outF) { IL2CPP.il2cpp_field_get_value(objPtr, field, (void*)pf); }
                        return true;
                    }
                }
            }
            return false;
        }

        // Writing a struct (Vector/Quaternion/Color) through the Il2CppInterop managed setter ALSO truncates to the
        // first float (set localPosition=(5,6,7) -> object becomes (5,0,0)). Pass the struct natively instead:
        // il2cpp_runtime_invoke with argv[0] = pointer to the unboxed float data (property setter), or
        // il2cpp_field_set_value (field).
        private static unsafe bool WriteStructProp(IntPtr objPtr, string setter, float[] vals)
        {
            if (objPtr == IntPtr.Zero) return false;
            IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
            if (klass == IntPtr.Zero) return false;
            IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, setter, 1);
            if (method == IntPtr.Zero) return false;
            fixed (float* p = vals)
            {
                void** argv = stackalloc void*[1];
                argv[0] = (void*)p;
                IntPtr exc = IntPtr.Zero;
                IL2CPP.il2cpp_runtime_invoke(method, objPtr, argv, ref exc);
                return exc == IntPtr.Zero;
            }
        }
        private static unsafe bool WriteStructField(IntPtr objPtr, string fieldName, float[] vals)
        {
            if (objPtr == IntPtr.Zero) return false;
            IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
            for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(k, fieldName);
                if (field != IntPtr.Zero) { fixed (float* p = vals) { IL2CPP.il2cpp_field_set_value(objPtr, field, (void*)p); } return true; }
            }
            return false;
        }
        private static int StructFloatCount(Type t)
        {
            switch ((Nullable.GetUnderlyingType(t) ?? t).Name)
            { case "Vector2": return 2; case "Vector3": return 3; case "Vector4": case "Quaternion": case "Color": case "Rect": return 4; default: return 0; }
        }
        // Try to write a struct member from a comma string "x,y,z[,w]" via native il2cpp. Returns false if not a struct.
        private static bool TryStructWrite(object casted, string name, Type memberType, string raw, bool isProperty)
        {
            int n = StructFloatCount(memberType);
            if (n == 0) return false;
            var ob = casted as Il2CppObjectBase;
            if (ob == null) return false;
            float[] vals;
            try { var f = Floats(raw, n); vals = new float[n]; for (int i = 0; i < n; i++) vals[i] = f[i]; }
            catch { return false; }
            return isProperty ? WriteStructProp(ob.Pointer, "set_" + name, vals) : WriteStructField(ob.Pointer, name, vals);
        }

        private static string StructValueOrNull(object casted, string memberName, Type memberType)
        {
            var u = Nullable.GetUnderlyingType(memberType) ?? memberType;
            int n;
            switch (u.Name)
            {
                case "Vector2": n = 2; break;
                case "Vector3": n = 3; break;
                case "Vector4": case "Quaternion": case "Color": case "Rect": n = 4; break;
                default: return null;
            }
            var ob = casted as Il2CppObjectBase;
            if (ob == null) return null;
            var f = new float[n];
            try { if (!ReadStructFloats(ob.Pointer, "get_" + memberName, memberName, n, f)) return null; }
            catch { return null; }
            var parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = f[i].ToString("0.###", CultureInfo.InvariantCulture);
            return "(" + string.Join(", ", parts) + ")";
        }

        // UnityExplorer's il2cpp member blacklist: these THROW or even CRASH il2cpp when read/enumerated (the
        // deprecated UnityEngine.Component.animation/audio/camera/collider/renderer/rigidbody/... shortcuts exist
        // on EVERY component and throw on read; the rest crash on access/GetParameters). Skip them entirely.
        private static readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine.MonoBehaviour.allowPrefabModeInPlayMode", "UnityEngine.MonoBehaviour.runInEditMode",
            "UnityEngine.Component.animation", "UnityEngine.Component.audio", "UnityEngine.Component.camera",
            "UnityEngine.Component.collider", "UnityEngine.Component.collider2D", "UnityEngine.Component.constantForce",
            "UnityEngine.Component.hingeJoint", "UnityEngine.Component.light", "UnityEngine.Component.networkView",
            "UnityEngine.Component.particleSystem", "UnityEngine.Component.renderer", "UnityEngine.Component.rigidbody",
            "UnityEngine.Component.rigidbody2D", "UnityEngine.Light.flare",
            "Il2CppSystem.Type.DeclaringMethod", "Il2CppSystem.RuntimeType.DeclaringMethod",
            "Unity.Jobs.LowLevel.Unsafe.JobsUtility.CreateJobReflectionData", "Unity.Profiling.ProfilerRecorder.CopyTo",
            "Unity.Profiling.ProfilerRecorder.StartNew", "UnityEngine.Analytics.Analytics.RegisterEvent",
            "UnityEngine.Analytics.Analytics.SendEvent", "UnityEngine.Analytics.ContinuousEvent.ConfigureEvent",
            "UnityEngine.AssetBundle.RecompressAssetBundleAsync", "UnityEngine.Audio.AudioMixerPlayable.Create",
            "UnityEngine.BoxcastCommand.ScheduleBatch", "UnityEngine.Camera.CalculateProjectionMatrixFromPhysicalProperties",
            "UnityEngine.Canvas.renderingDisplaySize", "UnityEngine.CapsulecastCommand.ScheduleBatch",
            "UnityEngine.Collider2D.Cast", "UnityEngine.Collider2D.Raycast", "UnityEngine.ComputeBuffer.BeginBufferWrite",
            "UnityEngine.ComputeBuffer.EndBufferWrite", "UnityEngine.Cubemap.SetPixelDataImpl", "UnityEngine.Cubemap.SetPixelDataImplArray",
            "UnityEngine.CubemapArray.SetPixelDataImpl", "UnityEngine.CubemapArray.SetPixelDataImplArray",
            "UnityEngine.GUI.DoButtonGrid", "UnityEngine.GUI.Slider", "UnityEngine.GUI.Toolbar",
            "UnityEngine.Graphics.DrawMeshInstancedIndirect", "UnityEngine.Graphics.DrawMeshInstancedProcedural",
            "UnityEngine.Graphics.DrawProcedural", "UnityEngine.Graphics.DrawProceduralIndirect",
            "UnityEngine.Graphics.DrawProceduralIndirectNow", "UnityEngine.Graphics.DrawProceduralNow",
            "UnityEngine.LineRenderer.BakeMesh", "UnityEngine.Mesh.GetIndices", "UnityEngine.Mesh.GetTriangles",
            "UnityEngine.Mesh.SetIndices", "UnityEngine.Mesh.SetTriangles", "UnityEngine.Physics2D.BoxCast",
            "UnityEngine.Physics2D.CapsuleCast", "UnityEngine.Physics2D.CircleCast", "UnityEngine.PhysicsScene.BoxCast",
            "UnityEngine.PhysicsScene.CapsuleCast", "UnityEngine.PhysicsScene.OverlapBox", "UnityEngine.PhysicsScene.OverlapCapsule",
            "UnityEngine.PhysicsScene.SphereCast", "UnityEngine.PhysicsScene2D.BoxCast", "UnityEngine.PhysicsScene2D.CapsuleCast",
            "UnityEngine.PhysicsScene2D.CircleCast", "UnityEngine.PhysicsScene2D.GetRayIntersection", "UnityEngine.PhysicsScene2D.Linecast",
            "UnityEngine.PhysicsScene2D.OverlapArea", "UnityEngine.PhysicsScene2D.OverlapBox", "UnityEngine.PhysicsScene2D.OverlapCapsule",
            "UnityEngine.PhysicsScene2D.OverlapCircle", "UnityEngine.PhysicsScene2D.OverlapCollider", "UnityEngine.PhysicsScene2D.OverlapPoint",
            "UnityEngine.PhysicsScene2D.Raycast", "UnityEngine.Playables.Playable.Create", "UnityEngine.Profiling.CustomSampler.Create",
            "UnityEngine.RaycastCommand.ScheduleBatch", "UnityEngine.RemoteConfigSettings.QueueConfig",
            "UnityEngine.RenderTexture.GetTemporaryImpl", "UnityEngine.Rendering.AsyncGPUReadback.Request",
            "UnityEngine.Rendering.AttachmentDescriptor.ConfigureClear", "UnityEngine.Rendering.BatchRendererGroup.AddBatch",
            "UnityEngine.Rendering.BatchRendererGroup.AddBatch_Injected", "UnityEngine.Rendering.CommandBuffer.DispatchRays",
            "UnityEngine.Rendering.CommandBuffer.DrawMeshInstancedProcedural", "UnityEngine.Rendering.CommandBuffer.Internal_DispatchRays",
            "UnityEngine.Rendering.CommandBuffer.ResolveAntiAliasedSurface", "UnityEngine.Rendering.ScriptableRenderContext.BeginRenderPass",
            "UnityEngine.Rendering.ScriptableRenderContext.BeginScopedRenderPass", "UnityEngine.Rendering.ScriptableRenderContext.BeginScopedSubPass",
            "UnityEngine.Rendering.ScriptableRenderContext.BeginSubPass", "UnityEngine.Rendering.ScriptableRenderContext.SetupCameraProperties",
            "UnityEngine.Rigidbody2D.Cast", "UnityEngine.Scripting.GarbageCollector.CollectIncremental",
            "UnityEngine.SpherecastCommand.ScheduleBatch", "UnityEngine.Texture.GetPixelDataSize", "UnityEngine.Texture.GetPixelDataOffset",
            "UnityEngine.Texture2D.SetPixelDataImpl", "UnityEngine.Texture2D.SetPixelDataImplArray",
            "UnityEngine.Texture2DArray.SetPixelDataImpl", "UnityEngine.Texture2DArray.SetPixelDataImplArray",
            "UnityEngine.Texture3D.SetPixelDataImpl", "UnityEngine.Texture3D.SetPixelDataImplArray",
            "UnityEngine.TrailRenderer.BakeMesh", "UnityEngine.WWW.LoadFromCacheOrDownload", "UnityEngine.XR.InputDevice.SendHapticImpulse",
        };
        private static bool IsBlacklisted(MemberInfo m)
        {
            var dt = m.DeclaringType;
            if (dt == null || string.IsNullOrEmpty(dt.Namespace)) return false;   // game types are never blacklisted
            return _blacklist.Contains(dt.FullName + "." + m.Name);
        }

        // Hide il2cpp/interop noise from the inspector (raw pointers, base Il2CppObjectBase plumbing).
        private static bool Hidden(Type memberType, Type declaringType)
        {
            if (memberType == typeof(IntPtr) || memberType == typeof(UIntPtr)) return true;
            if (declaringType == typeof(Il2CppObjectBase)) return true;
            string n = memberType.Name;
            return n == "IntPtr" || n == "UIntPtr" || n == "Il2CppObjectBase";
        }

        // ========================= members =========================

        private static void DumpMembers(object c, Type t, StringBuilder sb)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (p.GetIndexParameters().Length != 0 || !p.CanRead || Hidden(p.PropertyType, p.DeclaringType)) continue;
                string v; try { v = Stringify(p.GetValue(c)); } catch (Exception e) { v = "<err: " + e.Message + ">"; }
                sb.Append(p.CanWrite ? "prop " : "prop(ro) ").Append(p.Name).Append('\t').Append(p.PropertyType.Name).Append('\t').Append(v).Append('\n');
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (Hidden(f.FieldType, f.DeclaringType)) continue;
                string v; try { v = Stringify(f.GetValue(c)); } catch (Exception e) { v = "<err: " + e.Message + ">"; }
                sb.Append("field ").Append(f.Name).Append('\t').Append(f.FieldType.Name).Append('\t').Append(v).Append('\n');
            }
        }

        // Unified member dump with a ValueState tag (bool/number/string/enum/color/vector/collection/object) so
        // the client can render the right editor (UnityExplorer's InteractiveValue model). Format:
        //   "rw|ro\tkind\tName\tTypeName\tstate\tvalue"
        private static void DumpMembersTyped(object c, Type t, StringBuilder sb)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (p.GetIndexParameters().Length != 0 || !p.CanRead || Hidden(p.PropertyType, p.DeclaringType) || IsBlacklisted(p)) continue;
                string v = StructValueOrNull(c, p.Name, p.PropertyType);
                if (v == null) { try { v = Stringify(p.GetValue(c)); } catch (Exception e) { v = "<err: " + (e.InnerException?.Message ?? e.Message) + ">"; } }
                sb.Append(p.CanWrite ? "rw" : "ro").Append("\tprop\t").Append(p.Name).Append('\t')
                  .Append(p.PropertyType.Name).Append('\t').Append(StateOf(p.PropertyType)).Append('\t').Append(v).Append('\n');
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (Hidden(f.FieldType, f.DeclaringType) || IsBlacklisted(f)) continue;
                string v = StructValueOrNull(c, f.Name, f.FieldType);
                if (v == null) { try { v = Stringify(f.GetValue(c)); } catch (Exception e) { v = "<err: " + e.Message + ">"; } }
                sb.Append(f.IsInitOnly ? "ro" : "rw").Append("\tfield\t").Append(f.Name).Append('\t')
                  .Append(f.FieldType.Name).Append('\t').Append(StateOf(f.FieldType)).Append('\t').Append(v).Append('\n');
            }
        }

        private static string StateOf(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(bool)) return "bool";
            if (u.IsEnum) return "enum";
            if (u == typeof(int) || u == typeof(uint) || u == typeof(long) || u == typeof(ulong) || u == typeof(short)
                || u == typeof(ushort) || u == typeof(byte) || u == typeof(sbyte) || u == typeof(float) || u == typeof(double))
                return "number";
            if (u == typeof(string) || u == typeof(char)) return "string";
            string n = u.Name;
            if (n == "Color" || n == "Color32") return "color";
            if (n == "LayerMask") return "number";
            if (n == "Vector2" || n == "Vector3" || n == "Vector4" || n == "Quaternion" || n == "Rect") return "vector";
            if (u != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(u)) return "collection";
            return "object";
        }

        private static void DumpMethods(object c, Type t, StringBuilder sb)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (m.IsSpecialName || m.IsGenericMethod || m.DeclaringType == typeof(object) || IsBlacklisted(m)) continue;
                var ps = m.GetParameters();
                sb.Append(m.Name).Append('\t').Append(m.ReturnType.Name).Append("\t(");
                for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(','); sb.Append(ps[i].ParameterType.Name); }
                sb.Append(")\n");
            }
        }

        private static object GetMemberValue(object c, Type t, string name)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (p != null && p.CanRead) return p.GetValue(c);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (f != null) return f.GetValue(c);
            return null;
        }

        // Enumerate a collection via its Count/Length + int indexer (covers Il2Cpp List<T>, arrays, IList). row: "i \t value".
        private static void EnumerateCollection(object coll, int start, int count, StringBuilder sb)
        {
            Type ct = coll.GetType();
            var countProp = ct.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)
                         ?? ct.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
            var itemProp = ct.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo getItem = itemProp != null ? itemProp.GetGetMethod() : null;
            if (getItem == null) { try { getItem = ct.GetMethod("get_Item", new[] { typeof(int) }); } catch { } }
            if (countProp != null && getItem != null)
            {
                int n; try { n = Convert.ToInt32(countProp.GetValue(coll)); } catch { n = 0; }
                sb.Append("count\t").Append(n).Append('\t').Append(ct.Name).Append('\n');
                for (int i = start; i < n && i < start + count; i++)
                {
                    object el; try { el = getItem.Invoke(coll, new object[] { i }); } catch (Exception e) { el = "<err: " + (e.InnerException?.Message ?? e.Message) + ">"; }
                    sb.Append(i).Append('\t').Append(Sanitize(Stringify(el))).Append('\n');
                }
                return;
            }
            sb.Append("ERR no Count/Length + int indexer on ").Append(ct.FullName).Append('\n');
        }

        private static string GetMember(object c, Type t, string name)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (p != null && p.CanRead) return StructValueOrNull(c, name, p.PropertyType) ?? Stringify(p.GetValue(c));
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (f != null) return StructValueOrNull(c, name, f.FieldType) ?? Stringify(f.GetValue(c));
            return "ERR member not found";
        }

        private static string SetMember(object c, Type t, string name, string raw)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (p != null && p.CanWrite)
            {
                if (TryStructWrite(c, name, p.PropertyType, raw, true)) return "ok " + name + " = " + (StructValueOrNull(c, name, p.PropertyType) ?? "set");
                p.SetValue(c, ParseValue(p.PropertyType, raw));
                return "ok " + name + " = " + (StructValueOrNull(c, name, p.PropertyType) ?? Stringify(p.GetValue(c)));
            }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (f != null)
            {
                if (TryStructWrite(c, name, f.FieldType, raw, false)) return "ok " + name + " = " + (StructValueOrNull(c, name, f.FieldType) ?? "set");
                f.SetValue(c, ParseValue(f.FieldType, raw));
                return "ok " + name + " = " + (StructValueOrNull(c, name, f.FieldType) ?? Stringify(f.GetValue(c)));
            }
            return "ERR settable member not found";
        }

        private static string CallMethod(object c, Type t, string name, string[] args)
        {
            MethodInfo chosen = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (m.Name != name || m.IsGenericMethod) continue;
                if (m.GetParameters().Length == args.Length) { chosen = m; break; }
            }
            if (chosen == null) return "ERR no '" + name + "' overload taking " + args.Length + " arg(s)";
            var ps = chosen.GetParameters();
            var vals = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) vals[i] = ParseValue(ps[i].ParameterType, args[i]);
            var ret = chosen.Invoke(c, vals);
            return chosen.ReturnType == typeof(void) ? "ok (void)" : "ok -> " + Stringify(ret);
        }

        // ========================= harmony hooks =========================

        private static string AddHook(Type type, string method, bool block)
        {
            if (_harmony == null) return "ERR harmony unavailable";
            var mi = AccessTools.Method(type, method);
            if (mi == null)
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                    if (m.Name == method && !m.IsGenericMethod) { mi = m; break; }
            }
            if (mi == null) return "ERR method '" + method + "' not found on " + type.Name;
            string key = type.Name + "." + method;
            lock (_hooks)
            {
                if (_hooks.ContainsKey(key)) return "ERR already hooked: " + key;
                var prefix = new HarmonyMethod(typeof(MelonBridge).GetMethod(
                    block ? nameof(BlockPrefix) : nameof(LogPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                _harmony.Patch(mi, prefix: prefix);
                _hooks[key] = new HookEntry { Method = mi, Block = block };
            }
            return "ok hooked " + key + (block ? " (block)" : " (log)");
        }

        private static string RemoveHook(string key)
        {
            lock (_hooks)
            {
                if (!_hooks.TryGetValue(key, out var e)) return "ERR no such hook: " + key;
                try { _harmony.Unpatch(e.Method, HarmonyPatchType.Prefix, "MelonBridge"); } catch { }
                _hooks.Remove(key);
            }
            return "ok unhooked " + key;
        }

        private static void RecordHit(MethodBase m)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + (m?.DeclaringType?.Name) + "." + (m?.Name);
            lock (_hookLog) { _hookLog.Enqueue(line); while (_hookLog.Count > 1000) _hookLog.Dequeue(); }
        }

        private static void LogPrefix(MethodBase __originalMethod) => RecordHit(__originalMethod);

        private static bool BlockPrefix(MethodBase __originalMethod) { RecordHit(__originalMethod); return false; }

        // ========================= C# console (Roslyn runtime scripting) =========================
        // Mono.CSharp (mcs) is unusable here: its SkipVisibilityExt does a Harmony patch at init that our
        // HarmonyX rejects ("Patch Method must be a Static Method!"). Roslyn scripting runs cleanly on net8
        // CoreCLR. REPL state (declared vars/usings) persists across eval calls via ScriptState.ContinueWith.

        private static Microsoft.CodeAnalysis.Scripting.ScriptOptions _scriptOpts;
        private static Microsoft.CodeAnalysis.Scripting.ScriptState<object> _scriptState;

        private static Microsoft.CodeAnalysis.Scripting.ScriptOptions BuildScriptOptions()
        {
            var asms = new List<Assembly>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) asms.Add(a); }
                catch { }
            }
            return Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
                .WithReferences(asms)
                .WithImports("System", "System.Linq", "System.Text", "System.Collections",
                             "System.Collections.Generic", "UnityEngine");
        }

        // Compile+run pasted C# at runtime via Roslyn. Captures Console output + the return value. Runs on the
        // main thread (via Pump) so user code can touch Il2Cpp safely.
        private static string EvalCSharp(string code)
        {
            var outBuf = new StringWriter();
            var prevOut = Console.Out;
            object retVal = null;
            try
            {
                if (_scriptOpts == null) _scriptOpts = BuildScriptOptions();
                Console.SetOut(outBuf);
                _scriptState = _scriptState == null
                    ? Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.RunAsync(code, _scriptOpts).GetAwaiter().GetResult()
                    : _scriptState.ContinueWithAsync(code).GetAwaiter().GetResult();
                retVal = _scriptState.ReturnValue;
            }
            catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ce)
            {
                Console.SetOut(prevOut);
                return "COMPILE ERROR:\n" + string.Join("\n", ce.Diagnostics) + "\n";
            }
            catch (Exception e)
            {
                Console.SetOut(prevOut);
                var inner = e.InnerException ?? e;
                return "EXCEPTION: " + inner.GetType().Name + ": " + inner.Message + "\n" + outBuf;
            }
            finally { Console.SetOut(prevOut); }

            var sb = new StringBuilder();
            string outp = outBuf.ToString();
            if (!string.IsNullOrEmpty(outp)) sb.Append(outp);
            if (retVal != null) { if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n'); sb.Append("=> ").Append(Sanitize(retVal.ToString())).Append('\n'); }
            if (sb.Length == 0) sb.Append("ok\n");
            return sb.ToString();
        }

        // ========================= value (de)serialization =========================

        private static string Stringify(object o)
        {
            if (o == null) return "null";
            var uo = o as UnityEngine.Object;
            if (uo != null) { try { return Sanitize(o.ToString() + " #" + uo.GetInstanceID()); } catch { return Sanitize(o.ToString()); } }
            return Sanitize(o.ToString());
        }

        // The wire format is tab-delimited rows ending in newline. Some il2cpp ToString()s (Matrix4x4, multi-line
        // structs) embed \t and \n, which would corrupt the client's line/column parser. Flatten them to spaces.
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        }

        private static object ParseValue(Type t, string s)
        {
            if (t == typeof(string)) return s;
            if (s == "null") return null;
            var u = Nullable.GetUnderlyingType(t) ?? t;

            if (u.IsEnum) return int.TryParse(s, out int ev) ? Enum.ToObject(u, ev) : Enum.Parse(u, s, true);
            if (u == typeof(bool)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (u == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(uint)) return uint.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(long)) return long.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(ulong)) return ulong.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(short)) return short.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(ushort)) return ushort.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(byte)) return byte.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(sbyte)) return sbyte.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(float)) return float.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(double)) return double.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(char)) return s.Length > 0 ? s[0] : '\0';

            if (u == typeof(Vector2)) { var f = Floats(s, 2); return new Vector2(f[0], f[1]); }
            if (u == typeof(Vector3)) { var f = Floats(s, 3); return new Vector3(f[0], f[1], f[2]); }
            if (u == typeof(Vector4)) { var f = Floats(s, 4); return new Vector4(f[0], f[1], f[2], f[3]); }
            if (u == typeof(Quaternion)) { var f = Floats(s, 4); return new Quaternion(f[0], f[1], f[2], f[3]); }
            if (u == typeof(Color)) { var f = Floats(s, 3); return new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f); }
            if (u == typeof(Rect)) { var f = Floats(s, 4); return new Rect(f[0], f[1], f[2], f[3]); }
            if (u == typeof(Color32)) { var f = Floats(s, 3); Func<float, byte> b = x => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(x <= 1f ? x * 255f : x))); return new Color32(b(f[0]), b(f[1]), b(f[2]), f.Length > 3 ? b(f[3]) : (byte)255); }
            if (u == typeof(LayerMask)) { var lm = new LayerMask(); lm.value = int.Parse(s, CultureInfo.InvariantCulture); return lm; }
            if (u == typeof(decimal)) return decimal.Parse(s, CultureInfo.InvariantCulture);
            if (u == typeof(DateTime)) return DateTime.Parse(s, CultureInfo.InvariantCulture);

            if (typeof(Il2CppObjectBase).IsAssignableFrom(u))
            {
                int id = int.Parse(s, CultureInfo.InvariantCulture);
                var found = FindUnityObject(u, id);
                if (found == null) throw new Exception("no " + u.Name + " with instance id " + id);
                return found;
            }
            throw new Exception("unsupported type " + t.FullName);
        }

        private static float[] Floats(string s, int min)
        {
            var p = s.Split(',');
            if (p.Length < min) throw new Exception("expected " + min + " comma-separated numbers, got '" + s + "'");
            var f = new float[p.Length];
            for (int i = 0; i < p.Length; i++) f[i] = float.Parse(p[i].Trim(), CultureInfo.InvariantCulture);
            return f;
        }

        // Find an Il2Cpp runtime type from a managed proxy name (FullName or short Name) across loaded assemblies.
        private static Il2CppSystem.Type ResolveIl2CppType(string name)
        {
            Type proxy = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(name, false); } catch { }
                if (t == null)
                {
                    try { foreach (var c in asm.GetTypes()) if (c.Name == name) { t = c; break; } } catch { }
                }
                if (t != null && typeof(Il2CppObjectBase).IsAssignableFrom(t)) { proxy = t; break; }
            }
            return proxy == null ? null : Il2CppType.From(proxy);
        }

        // Find a managed Il2Cpp proxy Type by FullName or short Name across all loaded assemblies.
        private static Type FindProxyType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(name, false); } catch { }
                if (t == null) { try { foreach (var c in asm.GetTypes()) if (c.Name == name) { t = c; break; } } catch { } }
                if (t != null && typeof(Il2CppObjectBase).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        // Cached sorted list of all Il2Cpp type short-names, for type-search autocomplete (built once, lazily).
        private static List<string> _typeNames;
        private static void EnsureTypeNames()
        {
            if (_typeNames != null) return;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { ts = e.Types; }
                catch { continue; }
                foreach (var t in ts)
                {
                    if (t == null) continue;
                    try { if (typeof(Il2CppObjectBase).IsAssignableFrom(t) && !string.IsNullOrEmpty(t.Name)) set.Add(t.Name); }
                    catch { }
                }
            }
            _typeNames = new List<string>(set);
            _typeNames.Sort(StringComparer.OrdinalIgnoreCase);
        }

        // Like FindProxyType but accepts ANY type (static classes, non-Unity types) — prefers Il2Cpp proxies.
        private static Type FindAnyType(string name)
        {
            Type any = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(name, false); } catch { }
                if (t == null) { try { foreach (var c in asm.GetTypes()) if (c.Name == name || c.FullName == name) { t = c; break; } } catch { } }
                if (t != null) { if (typeof(Il2CppObjectBase).IsAssignableFrom(t)) return t; if (any == null) any = t; }
            }
            return any;
        }

        // All methods of a type (public+nonpublic, static+instance, walking the hierarchy) for the Hook Manager
        // class browser. row: name \t ret \t (sig) \t static|instance \t declaringType
        private static void DumpMethodsOfType(Type t, StringBuilder sb)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var seen = new HashSet<string>();
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (cur == typeof(Il2CppObjectBase)) break;   // skip interop plumbing
                MethodInfo[] ms; try { ms = cur.GetMethods(flags); } catch { continue; }
                foreach (var m in ms)
                {
                    if (m.IsSpecialName || m.IsGenericMethod || IsBlacklisted(m)) continue;
                    var ps = m.GetParameters();
                    var sig = new StringBuilder("(");
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) sig.Append(','); sig.Append(ps[i].ParameterType.Name); }
                    sig.Append(')');
                    string key = m.Name + sig;
                    if (!seen.Add(key)) continue;
                    sb.Append(m.Name).Append('\t').Append(m.ReturnType.Name).Append('\t').Append(sig)
                      .Append('\t').Append(m.IsStatic ? "static" : "instance").Append('\t').Append(cur.Name).Append('\n');
                }
            }
        }

        // Static fields/properties of a type (no instance), same typed format as DumpMembersTyped.
        private static void DumpStaticMembersTyped(Type t, StringBuilder sb)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            foreach (var p in t.GetProperties(F))
            {
                if (p.GetIndexParameters().Length != 0 || !p.CanRead || Hidden(p.PropertyType, p.DeclaringType)) continue;
                string v; try { v = Stringify(p.GetValue(null)); } catch (Exception e) { v = "<err: " + e.Message + ">"; }
                sb.Append(p.CanWrite ? "rw" : "ro").Append("\tprop\t").Append(p.Name).Append('\t')
                  .Append(p.PropertyType.Name).Append('\t').Append(StateOf(p.PropertyType)).Append('\t').Append(v).Append('\n');
            }
            foreach (var f in t.GetFields(F))
            {
                if (Hidden(f.FieldType, f.DeclaringType)) continue;
                string v; try { v = Stringify(f.GetValue(null)); } catch (Exception e) { v = "<err: " + e.Message + ">"; }
                sb.Append(f.IsInitOnly || f.IsLiteral ? "ro" : "rw").Append("\tfield\t").Append(f.Name).Append('\t')
                  .Append(f.FieldType.Name).Append('\t').Append(StateOf(f.FieldType)).Append('\t').Append(v).Append('\n');
            }
        }

        private static object FindUnityObject(Type proxyType, int id)
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.From(proxyType));
            var tryCast = typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(proxyType);
            foreach (var o in all)
            {
                var uo = o.TryCast<UnityEngine.Object>();
                if (uo != null && uo.GetInstanceID() == id) return tryCast.Invoke(o, null);
            }
            return null;
        }

        // ========================= bg thread: TCP, strings only =========================

        private static void ServerLoop()
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                MelonLogger.Msg("[MelonBridge] listening on 0.0.0.0:" + Port);
                // THREAD-PER-CLIENT: accept in a tight loop and hand each connection to its own reader thread.
                // The previous single-client loop stayed inside one client's read loop forever, so a persistent
                // client (the PC explorer) blocked every other connection (headset view, diagnostics) in the OS
                // backlog until they timed out + leaked as CLOSE_WAIT. All Il2Cpp work still funnels through the
                // ConcurrentQueue+Pump on the main thread, so N reader threads enqueueing is safe.
                while (_run)
                {
                    if (!listener.Pending()) { Thread.Sleep(50); continue; }
                    var client = listener.AcceptTcpClient();
                    var t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "MelonBridge-client" };
                    t.Start();
                }
            }
            catch (Exception e) { MelonLogger.Warning("[MelonBridge] server stopped: " + e.Message); }
            finally { try { listener?.Stop(); } catch { } }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var ns = client.GetStream())
                using (var sr = new StreamReader(ns, Encoding.UTF8))
                using (var sw = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while (_run && (line = sr.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        var req = new Req { Cmd = line };
                        Queue.Enqueue(req);
                        if (req.Done.Wait(8000)) sw.Write(req.Resp);
                        else sw.Write("ERR timeout (main thread busy)\n<<END>>\n");
                    }
                }
            }
            catch { /* client disconnected / socket reset - just drop this thread */ }
        }
    }
}
