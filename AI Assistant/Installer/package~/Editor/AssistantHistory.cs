using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VrcAvatarAssistant
{
    [Serializable] public class HistoryEvent
    {
        public string utc, author, kind, target, detail, task;
        public int count = 1;
    }
    [Serializable] public class HistorySettings
    {
        public bool paused;
        public int maxMegabytes = 16;
        public int archiveCount = 4;
    }
    [Serializable] public class ProjectProfile
    {
        public string unity, platform, avatarId, avatarName, task, status, packageManifest, packageLock;
        public string constraints = "";
    }
    [Serializable] public class CheckReport
    {
        public string utc, operation, avatar;
        public string status = "PARTIAL";
        public int missingScripts, missingReferences, renderers, missingMaterials;
        public bool expectedObjectFound, expectedComponentFound;
        public string[] manualChecks;
    }

    [InitializeOnLoad]
    public static class AssistantHistory
    {
        public const string Version = "1.1-alpha / editor 1.1.0-alpha.2";
        public static readonly string ProjectRoot = Directory.GetParent(Application.dataPath).FullName;
        public static readonly string Root = Path.Combine(ProjectRoot, "AI Assistant");
        public static readonly string HistoryRoot = Path.Combine(Root, "History");
        static readonly Dictionary<string, HistoryEvent> Pending = new Dictionary<string, HistoryEvent>();
        static HistorySettings settings;
        static double nextFlush, nextPackages;
        static string lastPackages;
        static int aiDepth;
        static bool stopped, conflicted, writing;
        static long revision;
        static string task = "", status = "IDLE", error = "";
        static Object avatar;
        static readonly Dictionary<string, string> Fingerprints = new Dictionary<string, string>();
        public static string Status => status;
        public static string Error => error;
        public static long Revision => revision;
        public static bool Conflict => conflicted;
        public static bool Recording => !Settings.paused;
        public static Object Avatar => avatar;
        public static HistorySettings Settings
        {
            get
            {
                if (settings == null)
                {
                    settings = new HistorySettings();
                    try { if (File.Exists(SettingsPath)) settings = JsonUtility.FromJson<HistorySettings>(File.ReadAllText(SettingsPath)) ?? settings; }
                    catch (Exception ex) { error = ex.Message; }
                }
                return settings;
            }
        }
        static string SettingsPath => Path.Combine(HistoryRoot, "settings.json");
        public static string Id(Object obj) => obj == null ? "deleted-or-unresolved" : GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
        static AssistantHistory()
        {
            Undo.postprocessModifications += Modified;
            Undo.undoRedoPerformed += () => Record("undo-redo", "editor", Undo.GetCurrentGroupName(), "unknown");
            EditorApplication.hierarchyChanged += () => Record("hierarchy", "scene", "Hierarchy changed; see object events", "unknown");
            EditorSceneManager.sceneSaved += Saved;
            ObjectChangeEvents.changesPublished += Changed;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeReload;
            EditorApplication.delayCall += () =>
            {
                status = SessionState.GetString("VAA.task", "") == "" ? "IDLE" : "INTERRUPTED";
                task = SessionState.GetString("VAA.task", "");
                stopped = status == "INTERRUPTED";
                Record("editor-start", "editor", Version, "unknown");
                SaveProfile();
            };
        }
        static void BeforeReload()
        {
            if (status == "RUNNING") status = "INTERRUPTED";
            Flush();
        }
        static void Saved(Scene scene) => Record("scene-save", scene.path, scene.name, "unknown");
        static UndoPropertyModification[] Modified(UndoPropertyModification[] mods)
        {
            foreach (var m in mods)
            {
                var p = m.currentValue;
                if (p == null || p.target == null) continue;
                Record("property", Id(p.target), p.propertyPath, "unknown");
            }
            return mods;
        }
        static void Changed(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i);
                string target = "scene";
                switch (kind)
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                        target = Id(EditorUtility.InstanceIDToObject(created.instanceId)); break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var props);
                        target = Id(EditorUtility.InstanceIDToObject(props.instanceId)); break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        target = Id(EditorUtility.InstanceIDToObject(parent.instanceId)); break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure);
                        target = Id(EditorUtility.InstanceIDToObject(structure.instanceId)); break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed);
                        target = "destroyed-instance:" + destroyed.instanceId; break;
                }
                Record(kind.ToString(), target, "Undo-recorded object change", "unknown");
            }
        }
        public static void Record(string kind, string target, string detail, string author = "unknown")
        {
            if (writing) return;
            revision++;
            if (aiDepth > 0) author = "ai";
            // Explicit user notes block a task; object fingerprints detect unmarked edits.
            if (status == "RUNNING" && aiDepth == 0 && author == "user" && kind != "task")
            { conflicted = true; status = "CONFLICT"; }
            if (!Recording) return;
            string key = author + "|" + kind + "|" + target + "|" + detail;
            if (Pending.TryGetValue(key, out var existing)) { existing.count++; existing.utc = DateTime.UtcNow.ToString("o"); }
            else Pending[key] = new HistoryEvent { utc = DateTime.UtcNow.ToString("o"), author = author, kind = kind, target = target, detail = detail, task = task };
        }
        public static void MarkUserAction(string description)
        { Record("user-note", "editor", description, "user"); Flush(); }
        public static void SetPaused(bool paused)
        {
            Flush(); Settings.paused = paused;
            Directory.CreateDirectory(HistoryRoot);
            File.WriteAllText(SettingsPath, JsonUtility.ToJson(Settings, true));
        }
        public static void SetLimits(int megabytes, int archives)
        {
            Settings.maxMegabytes = Mathf.Clamp(megabytes, 1, 256);
            Settings.archiveCount = Mathf.Clamp(archives, 0, 20);
            Directory.CreateDirectory(HistoryRoot);
            File.WriteAllText(SettingsPath, JsonUtility.ToJson(Settings, true));
        }
        static void Tick()
        {
            if (EditorApplication.timeSinceStartup >= nextFlush)
            { nextFlush = EditorApplication.timeSinceStartup + 2; Flush(); }
            if (EditorApplication.timeSinceStartup < nextPackages || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            nextPackages = EditorApplication.timeSinceStartup + 10;
            string packages = ReadProjectFile("Packages/manifest.json") + ReadProjectFile("Packages/packages-lock.json");
            if (lastPackages != null && packages != lastPackages)
            { Record("packages-changed", "Packages", "Manifest or lock changed", "unknown"); SaveProfile(); }
            lastPackages = packages;
        }
        public static void Flush()
        {
            if (Pending.Count == 0 || writing) return;
            writing = true;
            try
            {
                Directory.CreateDirectory(HistoryRoot);
                string file = Path.Combine(HistoryRoot, "events.jsonl");
                long size = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (size >= Math.Max(1, Settings.maxMegabytes) * 1024L * 1024)
                {
                    int n = Mathf.Clamp(Settings.archiveCount, 0, 20);
                    if (n == 0) File.Delete(file);
                    else
                    {
                        string oldest = file + "." + n;
                        if (File.Exists(oldest)) File.Delete(oldest);
                        for (int i = n - 1; i >= 1; i--)
                            if (File.Exists(file + "." + i)) File.Move(file + "." + i, file + "." + (i + 1));
                        File.Move(file, file + ".1");
                    }
                }
                File.AppendAllLines(file, Pending.Values.Select(x => JsonUtility.ToJson(x)));
                Pending.Clear(); error = "";
            }
            catch (Exception ex) { error = ex.Message; if (Pending.Count > 2000) Pending.Clear(); }
            finally { writing = false; }
        }
        public static string Recent(int count = 30)
        {
            Flush(); var path = Path.Combine(HistoryRoot, "events.jsonl");
            if (!File.Exists(path)) return "";
            var queue = new Queue<string>();
            foreach (string line in File.ReadLines(path)) { queue.Enqueue(line); if (queue.Count > Math.Max(1, count)) queue.Dequeue(); }
            return string.Join("\n", queue);
        }
        static string Fingerprint(Object obj)
        {
            if (obj == null) return "missing";
            var go = obj as GameObject;
            if (go == null) return EditorJsonUtility.ToJson(obj);
            return string.Join("\n", go.GetComponentsInChildren<Transform>(true).SelectMany(t =>
                new[] { Id(t.gameObject) + ":" + EditorJsonUtility.ToJson(t.gameObject) }.Concat(
                    t.GetComponents<Component>().Select(c => c == null ? "missing-script" : Id(c) + ":" + EditorJsonUtility.ToJson(c)))));
        }
        public static void BeginTask(string taskId, GameObject target, string constraints = "")
        {
            if (status == "RUNNING" || status == "CONFLICT") throw new InvalidOperationException("A task is already active. End it first.");
            if (target == null || string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("Exact avatar and task id are required.");
            Flush(); task = taskId; avatar = target; stopped = false; conflicted = false; status = "RUNNING";
            Fingerprints.Clear(); Fingerprints[Id(target)] = Fingerprint(target);
            SessionState.SetString("VAA.task", task); SessionState.SetString("VAA.constraints", constraints);
            Record("task", Id(target), "begin", "ai"); SaveProfile();
        }
        public static void Stop()
        { stopped = true; status = "STOPPED"; Record("task", "editor", "stop", "user"); Flush(); SaveProfile(); }
        public static void EndTask(string result)
        { Record("task", "editor", "end: " + result, "ai"); status = "IDLE"; task = ""; SessionState.SetString("VAA.task", ""); Fingerprints.Clear(); Flush(); SaveProfile(); }
        public static void WatchDependencies(params Object[] dependencies)
        {
            if (status != "RUNNING") throw new InvalidOperationException("Begin the task first.");
            foreach (var obj in dependencies)
            {
                if (obj == null) throw new ArgumentException("Missing dependency.");
                string id = Id(obj);
                if (!Fingerprints.ContainsKey(id)) Fingerprints[id] = Fingerprint(obj);
            }
        }
        // Call this in the same synchronous main-thread command that performs the edit.
        // It is a cooperative guard, not a sandbox or an async author-identification mechanism.
        public static void RunAgentStep(Action action, params Object[] affected)
        {
            if (stopped || conflicted || status != "RUNNING") throw new InvalidOperationException("Task is stopped, interrupted, conflicting or absent.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Editor is not ready for a guarded write.");
            if (avatar == null || Fingerprint(avatar) != Fingerprints[Id(avatar)])
            { conflicted = true; status = "CONFLICT"; throw new InvalidOperationException("Avatar changed since the previous agent step; reread and start a new task."); }
            foreach (var obj in affected)
            {
                if (obj == null) throw new ArgumentException("Missing affected object.");
                string id = Id(obj), fp = Fingerprint(obj);
                if (Fingerprints.TryGetValue(id, out var old) && old != fp)
                { conflicted = true; status = "CONFLICT"; throw new InvalidOperationException("Affected dependency changed: " + id); }
                Fingerprints[id] = fp;
            }
            aiDepth++;
            try { action(); Record("agent-step", Id(avatar), "synchronous command", "ai"); }
            catch { stopped = true; status = "FAILED"; throw; }
            finally
            {
                aiDepth--;
                foreach (var obj in affected) if (obj != null) Fingerprints[Id(obj)] = Fingerprint(obj);
                if (avatar != null) Fingerprints[Id(avatar)] = Fingerprint(avatar);
                Flush(); SaveProfile();
            }
        }
        public static string ReadProjectFile(string path)
        { var file = Path.Combine(ProjectRoot, path); return File.Exists(file) ? File.ReadAllText(file) : ""; }
        public static void SaveProfile()
        {
            try
            {
                Directory.CreateDirectory(HistoryRoot);
                string profilePath = Path.Combine(HistoryRoot, "project-profile.json");
                ProjectProfile previous = File.Exists(profilePath) ? JsonUtility.FromJson<ProjectProfile>(File.ReadAllText(profilePath)) : null;
                var profile = new ProjectProfile { unity = Application.unityVersion, platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    avatarId = avatar == null ? previous?.avatarId ?? "" : Id(avatar), avatarName = avatar == null ? previous?.avatarName ?? "" : avatar.name, task = task, status = status,
                    constraints = SessionState.GetString("VAA.constraints", previous?.constraints ?? ""), packageManifest = ReadProjectFile("Packages/manifest.json"),
                    packageLock = ReadProjectFile("Packages/packages-lock.json") };
                File.WriteAllText(profilePath, JsonUtility.ToJson(profile, true));
            } catch (Exception ex) { error = ex.Message; }
        }
        public static string Check(GameObject target, string operation, string expectedChild = "", string expectedComponent = "")
        {
            if (target == null) throw new ArgumentException("Select the exact avatar.");
            if (!new[] { "clothing", "prop", "menu", "materials", "general" }.Contains(operation)) throw new ArgumentException("Unknown operation.");
            var report = new CheckReport { utc = DateTime.UtcNow.ToString("o"), operation = operation, avatar = Id(target) };
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                report.missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var so = new SerializedObject(c); var p = so.GetIterator();
                    while (p.NextVisible(true))
                        if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == null && p.objectReferenceInstanceIDValue != 0)
                            report.missingReferences++;
                }
            }
            var renderers = target.GetComponentsInChildren<Renderer>(true); report.renderers = renderers.Length;
            report.missingMaterials = renderers.Sum(r => r.sharedMaterials.Count(m => m == null));
            report.expectedObjectFound = expectedChild != "" && target.GetComponentsInChildren<Transform>(true).Any(t => t.name == expectedChild);
            report.expectedComponentFound = expectedComponent != "" && target.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().FullName == expectedComponent);
            report.manualChecks = operation == "clothing" ? new[] { "Rig/weights, clipping in poses, toggle, SDK build, target platform: NOT_TESTED" } :
                operation == "prop" ? new[] { "Attachment, scale, constraints, toggle, SDK build: NOT_TESTED" } :
                operation == "menu" ? new[] { "Expression parameters, controller transitions and budget, SDK build: NOT_TESTED" } :
                operation == "materials" ? new[] { "Shader platform support, appearance and shared references: NOT_TESTED" } :
                new[] { "VRChat SDK validation, build and in-client behaviour: NOT_TESTED" };
            if (report.missingScripts > 0 || report.missingReferences > 0 || report.missingMaterials > 0 ||
                (expectedChild != "" && !report.expectedObjectFound) || (expectedComponent != "" && !report.expectedComponentFound)) report.status = "FAILED";
            string json = JsonUtility.ToJson(report, true); Directory.CreateDirectory(Path.Combine(Root, "Reports"));
            File.WriteAllText(Path.Combine(Root, "Reports", "check-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json"), json);
            return json;
        }
    }
    public class AssistantAssets : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            foreach (var p in imported) AssistantHistory.Record("asset-import", p, "imported", "unknown");
            foreach (var p in deleted) AssistantHistory.Record("asset-delete", p, "deleted", "unknown");
            for (int i = 0; i < moved.Length; i++) AssistantHistory.Record("asset-move", moved[i], oldPaths[i], "unknown");
        }
    }
    public class AssistantWindow : EditorWindow
    {
        Vector2 scroll;
        string note = "", taskId = "", constraints = "", expected = "";
        int operation;
        [MenuItem("Tools/VRC Avatar Assistant/Open")]
        static void Open() => GetWindow<AssistantWindow>("VRC Assistant");
        void OnInspectorUpdate() => Repaint();
        void OnGUI()
        {
            GUILayout.Label(AssistantHistory.Version, EditorStyles.boldLabel);
            GUILayout.Label("Task: " + AssistantHistory.Status + " / revision " + AssistantHistory.Revision);
            GUILayout.Label("Recording: " + (AssistantHistory.Recording ? "ON" : "PAUSED"));
            if (AssistantHistory.Error != "") EditorGUILayout.HelpBox(AssistantHistory.Error, MessageType.Error);
            if (AssistantHistory.Conflict) EditorGUILayout.HelpBox("External change detected. Reread the avatar and end/restart the task.", MessageType.Warning);
            if (GUILayout.Button(AssistantHistory.Recording ? "Pause recording" : "Resume recording")) AssistantHistory.SetPaused(AssistantHistory.Recording);
            int mb = EditorGUILayout.IntField("Log file limit (MB)", AssistantHistory.Settings.maxMegabytes);
            int archives = EditorGUILayout.IntField("Retained log archives", AssistantHistory.Settings.archiveCount);
            if (GUILayout.Button("Save limits")) AssistantHistory.SetLimits(mb, archives);
            taskId = EditorGUILayout.TextField("Task id", taskId);
            constraints = EditorGUILayout.TextField("Constraints", constraints);
            if (GUILayout.Button("Begin task on selected avatar"))
                Try(() => AssistantHistory.BeginTask(taskId, Selection.activeGameObject, constraints));
            if (GUILayout.Button("STOP agent writes")) AssistantHistory.Stop();
            if (GUILayout.Button("End task")) AssistantHistory.EndTask("ended from UI");
            note = EditorGUILayout.TextField("My action / note", note);
            if (GUILayout.Button("Record my action")) { AssistantHistory.MarkUserAction(note); note = ""; }
            operation = EditorGUILayout.Popup("Check operation", operation, new[] { "general", "clothing", "prop", "menu", "materials" });
            expected = EditorGUILayout.TextField("Expected child name", expected);
            if (GUILayout.Button("Check selected avatar")) Try(() => AssistantHistory.Check(Selection.activeGameObject, new[] { "general", "clothing", "prop", "menu", "materials" }[operation], expected));
            if (GUILayout.Button("Refresh project / packages")) AssistantHistory.SaveProfile();
            if (GUILayout.Button("Open local history")) EditorUtility.RevealInFinder(AssistantHistory.HistoryRoot);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(RecentText());
            EditorGUILayout.EndScrollView();
        }
        string recent = "";
        double refresh;
        string RecentText()
        {
            if (EditorApplication.timeSinceStartup >= refresh)
            { refresh = EditorApplication.timeSinceStartup + 2; recent = AssistantHistory.Recent(20); }
            return recent;
        }
        static void Try(Action action) { try { action(); } catch (Exception ex) { Debug.LogError(ex.Message); } }
    }
}
