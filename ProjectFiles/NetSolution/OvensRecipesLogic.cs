#region Using directives
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.OPCUAClient;
using FTOptix.Alarm;
using FTOptix.Core;
#endregion

/// <summary>
/// Per-session recipe editor. Each UI session gets its own runtime OvenRecipe instance (exposed through the
/// screen alias <see cref="EditorAliasName"/>) used as edit buffer, so sessions can edit different recipes
/// independently. The auxiliary variables listed in <see cref="EditorVariables"/> live on the OvenRecipe type.
/// A Draft recipe can be edited by one session at a time (demo-grade lock).
/// </summary>
public class OvensRecipesLogic : BaseNetLogic
{
    private const string Cat = "OvensRecipesLogic";
    private const string SchemaPath = "Recipes/RecipeSchema";
    private const string EditorAliasName = "RecipeEditor";
    private const string SelectedAliasName = "SelectedRecipeId";
    private const string ListLogicPath = "div/RecipesView/RecipeList/RecipeListLogic";
    private const string ListRefreshMethod = "RefreshRecipeGroups";

    private static readonly string[] EditorVariables =
    {
        "Name", "Version", "Status", "Author", "Description", "Used", "IsLoaded", "IsEditable", "LockedBy", "LastResult"
    };

    // Mirrors the RecipeWorkflowStatus enum in Model/Enums.
    private enum Wf : uint { Draft = 0, InReview = 1, Approved = 2, Deprecated = 3 }

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, (NodeId Owner, string User)> Locks = new Dictionary<string, (NodeId, string)>();
    private static readonly Regex NameRe = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,39}$");
    private static readonly Regex VersionRe = new Regex(@"^\d{1,3}(\.\d{1,3})?$");

    private RecipeSchema schema;
    private IUAObject editor;
    private IUAVariable selectedAlias;
    private OvenRecipe buffer;
    private string lockKey;

    public override void Start()
    {
        schema = Project.Current.Get<RecipeSchema>(SchemaPath);
        if (schema == null)
        {
            Log.Error(Cat, $"Recipe schema '{SchemaPath}' not found");
            return;
        }

        buffer = InformationModel.MakeObject<OvenRecipe>("Editor");
        editor = buffer;

        var missing = new List<string>();
        foreach (var name in EditorVariables)
        {
            if (editor.GetVariable(name) == null) missing.Add(name);
        }
        if (missing.Count > 0)
        {
            Log.Error(Cat, $"Type OvenRecipe is missing the editor variables: {string.Join(", ", missing)}");
            return;
        }

        LogicObject.Add(editor);
        Owner.SetAlias(EditorAliasName, editor);

        // Selecting a row only sets the alias: load the recipe whenever it changes.
        selectedAlias = Owner.GetVariable(SelectedAliasName);
        if (selectedAlias != null) selectedAlias.VariableChange += OnSelectedChanged;
    }

    public override void Stop()
    {
        if (selectedAlias != null) selectedAlias.VariableChange -= OnSelectedChanged;
        Unlock();
    }

    private void OnSelectedChanged(object sender, VariableChangeEventArgs e)
    {
        LoadSelected();
    }

    #region Load / discard

    [ExportMethod]
    public void LoadRecipe(string name, string version)
    {
        Load(name, version);
    }

    [ExportMethod]
    public void LoadSelected()
    {
        var selected = Owner.GetAlias(SelectedAliasName);
        var name = selected?.GetVariable("Name")?.Value.Value as string;
        var version = selected?.GetVariable("Version")?.Value.Value as string;
        Load(name, version);
    }

    [ExportMethod]
    public void DiscardChanges()
    {
        Load(GetString("Name"), GetString("Version"));
    }

    #endregion

    #region Save / lifecycle

    [ExportMethod]
    public void SaveRecipe()
    {
        if (!TryGetLoaded(out var id)) return;
        if (!CurrentIsEditableDraft(id, out var error)) { Fail(error); return; }

        var validation = Validate();
        if (validation != null) { Fail(validation); return; }

        var before = ReadMetadata(id);
        var result = schema.TransferFromTargetToStore(id, buffer.NodeId, true, ErrorPolicy.Strict);
        if (result != TransferFromTargetToStoreResultCode.SuccessRecipeUpdated && result != TransferFromTargetToStoreResultCode.SuccessRecipeCreated)
        {
            Fail($"Save failed: {result}");
            return;
        }

        // Restore anything the store transfer may have reset, then apply the metadata edited on the page.
        foreach (var kv in before)
        {
            if (kv.Key == "Author" || kv.Key == "Description") continue;
            var now = GetMetadata(id, kv.Key);
            if (!Equals(Convert.ToString(now), Convert.ToString(kv.Value)))
            {
                Log.Warning(Cat, $"Metadata '{kv.Key}' changed by the transfer ({now} -> {kv.Value}), restoring");
                SetMetadata(id, kv.Key, kv.Value);
            }
        }
        SetMetadata(id, "Author", CurrentUser());
        SetMetadata(id, "Description", GetString("Description"));
        SetVar("Author", CurrentUser());

        Ok($"Saved {id.Name} v{id.Version}");
    }

    [ExportMethod]
    public void SetStatus(string newStatus)
    {
        if (!TryGetLoaded(out var id)) return;
        if (!Enum.TryParse<Wf>(newStatus, out var target)) { Fail($"Unknown status '{newStatus}'"); return; }

        var current = ReadStatus(id);
        if (current == null) { Fail("Cannot read the recipe status"); return; }
        if (!IsAllowed(current.Value, target)) { Fail($"Transition {current} -> {target} is not allowed"); return; }

        if (current == Wf.Draft && LockedByOther(id, out var holder)) { Fail($"Recipe is being edited by {holder}"); return; }

        if (!SetMetadata(id, "Status", (uint)target)) return;
        if (target != Wf.Draft) Unlock();
        Log.Info(Cat, $"{id.Name} v{id.Version}: {current} -> {target}");
        FleetOverviewCollector.Record("recipe" + target, $"{id.Name} v{id.Version}");
        Load(id.Name, id.Version);
        RefreshList();
        if (target == Wf.Approved || target == Wf.Deprecated)
            RecipeOrchestratorLogic.NotifyStatusChanged(id.Name, id.Version);
        Ok($"{id.Name} v{id.Version} is now {target}");
    }

    #endregion

    #region Create / duplicate / rename / delete

    /// <summary>Creates a new Draft recipe with the values currently shown in the buffer.</summary>
    [ExportMethod]
    public void NewRecipe(string name, string version)
    {
        if (!CheckIdentity(name, version)) return;
        var id = new RecipeId { Name = name, Version = version };

        var result = schema.TransferFromTargetToStore(id, buffer.NodeId, false, ErrorPolicy.Strict);
        if (result != TransferFromTargetToStoreResultCode.SuccessRecipeCreated) { Fail($"Create failed: {result}"); return; }

        InitMetadata(id, CurrentUser(), string.Empty);
        Load(name, version);
        RefreshList();
        Ok($"Created {name} v{version}");
    }

    [ExportMethod]
    public void DuplicateRecipe(string newName, string newVersion)
    {
        if (!TryGetLoaded(out var id)) return;
        if (!CheckIdentity(newName, newVersion)) return;
        var newId = new RecipeId { Name = newName, Version = newVersion };

        var result = schema.DuplicateRecipe(id, newId);
        if (result != DuplicateRecipeResultCode.Success) { Fail($"Duplicate failed: {result}"); return; }

        InitMetadata(newId, CurrentUser(), GetString("Description"));
        Load(newName, newVersion);
        RefreshList();
        Ok($"Created {newName} v{newVersion} from {id.Name} v{id.Version}");
    }

    [ExportMethod]
    public void RenameRecipe(string newName)
    {
        if (!TryGetLoaded(out var id)) return;
        if (!CurrentIsEditableDraft(id, out var error)) { Fail(error); return; }
        if (!CheckIdentity(newName, id.Version)) return;

        var result = schema.RenameRecipe(id, newName);
        if (result.ResultCode != RenameRecipeResultCode.Success) { Fail($"Rename failed: {result.ResultCode}"); return; }

        Unlock();
        Load(newName, id.Version);
        RefreshList();
        Ok($"Renamed to {newName} v{id.Version}");
    }

    [ExportMethod]
    public void DeleteRecipe()
    {
        if (!TryGetLoaded(out var id)) return;
        var status = ReadStatus(id);
        if (status == Wf.InReview || status == Wf.Approved) { Fail($"A {status} recipe cannot be deleted: deprecate it first"); return; }
        if (status == Wf.Draft && LockedByOther(id, out var holder)) { Fail($"Recipe is being edited by {holder}"); return; }
        if (GetMetadata(id, "Used") is bool used && used) { Fail("The recipe is in use"); return; }

        var result = schema.DeleteRecipe(id);
        if (result != DeleteRecipeResultCode.Success) { Fail($"Delete failed: {result}"); return; }

        Unlock();
        Clear();
        RefreshList();
        Ok($"Deleted {id.Name} v{id.Version}");
    }

    #endregion

    #region Core

    private void Load(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) { Fail("No recipe selected"); return; }
        Unlock();

        var id = new RecipeId { Name = name, Version = version };
        var result = schema.TransferFromStoreToTarget(id, buffer.NodeId, ErrorPolicy.Strict);
        if (result != TransferFromStoreToTargetResultCode.Success)
        {
            Clear();
            Fail($"Load failed: {result}");
            return;
        }

        var status = ReadStatus(id) ?? Wf.Draft;
        var locked = true;
        var holder = string.Empty;
        if (status == Wf.Draft) locked = TryLock(id, out holder);

        SetVar("Name", name);
        SetVar("Version", version);
        SetVar("Status", (uint)status);
        SetVar("Author", Convert.ToString(GetMetadata(id, "Author")) ?? string.Empty);
        SetVar("Description", Convert.ToString(GetMetadata(id, "Description")) ?? string.Empty);
        SetVar("Used", GetMetadata(id, "Used") is bool u && u);
        SetVar("IsLoaded", true);
        SetVar("IsEditable", status == Wf.Draft && locked);
        SetVar("LockedBy", locked ? string.Empty : holder);
        Ok(locked ? $"Loaded {name} v{version}" : $"Loaded {name} v{version} (read-only, edited by {holder})");
    }

    private void Clear()
    {
        SetVar("Name", string.Empty);
        SetVar("Version", string.Empty);
        SetVar("Status", 0u);
        SetVar("Author", string.Empty);
        SetVar("Description", string.Empty);
        SetVar("Used", false);
        SetVar("IsLoaded", false);
        SetVar("IsEditable", false);
        SetVar("LockedBy", string.Empty);
    }

    private bool TryGetLoaded(out RecipeId id)
    {
        id = default;
        if (!GetBool("IsLoaded")) { Fail("No recipe loaded"); return false; }
        id = new RecipeId { Name = GetString("Name"), Version = GetString("Version") };
        return true;
    }

    private bool CurrentIsEditableDraft(RecipeId id, out string error)
    {
        error = null;
        if (ReadStatus(id) != Wf.Draft) error = "Only a Draft recipe can be modified";
        else if (!GetBool("IsEditable") || lockKey == null) error = "The recipe is not locked by this session";
        return error == null;
    }

    private static bool IsAllowed(Wf from, Wf to)
    {
        return (from == Wf.Draft && to == Wf.InReview)
            || (from == Wf.InReview && (to == Wf.Approved || to == Wf.Draft))
            || (from == Wf.Approved && to == Wf.Deprecated);
    }

    private string Validate()
    {
        foreach (var p in new[] { ("PreHeat", buffer.PreHeat), ("Bake", buffer.Bake), ("Cooldown", buffer.Cooldown) })
        {
            if (p.Item2.TemperatureSetpoint < 0 || p.Item2.TemperatureSetpoint > 512) return $"{p.Item1}: setpoint must be 0-512";
            if (p.Item2.Duration < 1 || p.Item2.Duration > 7200) return $"{p.Item1}: duration must be 1-7200 s";
            if (p.Item2.FanInRamp > 100 || p.Item2.FanOutRamp > 100) return $"{p.Item1}: fan values must be 0-100";
        }
        return null;
    }

    private bool CheckIdentity(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(name) || !NameRe.IsMatch(name)) { Fail("Name: letters, digits, space, _ or - (max 40)"); return false; }
        if (string.IsNullOrWhiteSpace(version) || !VersionRe.IsMatch(version)) { Fail("Version must look like 1.0"); return false; }
        return true;
    }

    private void InitMetadata(RecipeId id, string author, string description)
    {
        SetMetadata(id, "Status", (uint)Wf.Draft);
        SetMetadata(id, "Used", false);
        SetMetadata(id, "Author", author ?? string.Empty);
        SetMetadata(id, "Description", description ?? string.Empty);
    }

    #endregion

    #region Store metadata

    private Wf? ReadStatus(RecipeId id)
    {
        var raw = GetMetadata(id, "Status");
        if (raw == null) return null;
        try { return (Wf)Convert.ToUInt32(raw); }
        catch (Exception) { return null; }
    }

    private object GetMetadata(RecipeId id, string name)
    {
        var r = schema.GetRecipeMetadataValue(id, name);
        if (r.ResultCode != GetRecipeMetadataValueResultCode.Success)
        {
            Log.Warning(Cat, $"Metadata '{name}' of {id.Name} v{id.Version}: {r.ResultCode}");
            return null;
        }
        return r.MetadataValue.Value;
    }

    private bool SetMetadata(RecipeId id, string name, object value)
    {
        var r = schema.SetRecipeMetadataValue(id, name, value);
        if (r != SetRecipeMetadataValueResultCode.Success)
        {
            Fail($"Metadata '{name}' not written: {r}");
            return false;
        }
        return true;
    }

    private Dictionary<string, object> ReadMetadata(RecipeId id)
    {
        var map = new Dictionary<string, object>();
        var r = schema.GetRecipeMetadataValues(id);
        if (r.ResultCode != GetRecipeMetadataValuesResultCode.Success) return map;
        foreach (var m in r.MetadataValues) map[m.Name] = m.Value;
        return map;
    }

    #endregion

    #region Locking

    private bool TryLock(RecipeId id, out string holder)
    {
        var key = id.Name + "@" + id.Version;
        lock (Gate)
        {
            if (Locks.TryGetValue(key, out var l) && l.Owner != LogicObject.NodeId)
            {
                holder = l.User;
                return false;
            }
            Locks[key] = (LogicObject.NodeId, CurrentUser());
            lockKey = key;
            holder = string.Empty;
            return true;
        }
    }

    private bool LockedByOther(RecipeId id, out string holder)
    {
        lock (Gate)
        {
            if (Locks.TryGetValue(id.Name + "@" + id.Version, out var l) && l.Owner != LogicObject.NodeId)
            {
                holder = l.User;
                return true;
            }
        }
        holder = string.Empty;
        return false;
    }

    private void Unlock()
    {
        lock (Gate)
        {
            if (lockKey != null && Locks.TryGetValue(lockKey, out var l) && l.Owner == LogicObject.NodeId) Locks.Remove(lockKey);
            lockKey = null;
        }
    }

    private string CurrentUser()
    {
        var name = Session?.User?.BrowseName;
        return string.IsNullOrEmpty(name) ? "unknown" : name;
    }

    #endregion

    #region Editor variables

    private void SetVar(string name, object value) => editor.GetVariable(name).Value = new UAValue(value);
    private string GetString(string name) => editor.GetVariable(name).Value.Value as string ?? string.Empty;
    private bool GetBool(string name) => editor.GetVariable(name).Value.Value is bool b && b;

    private void Ok(string message)
    {
        SetVar("LastResult", "OK: " + message);
        Log.Info(Cat, message);
    }

    private void Fail(string message)
    {
        SetVar("LastResult", "ERROR: " + message);
        Log.Warning(Cat, message);
    }

    private void RefreshList()
    {
        var listLogic = Owner.Get(ListLogicPath);
        if (listLogic == null)
        {
            Log.Warning(Cat, $"'{ListLogicPath}' not found: recipe list not refreshed");
            return;
        }
        ((IUAObject)listLogic).ExecuteMethod(ListRefreshMethod);
    }

    #endregion
}
