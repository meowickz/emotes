using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace EmotePlugin;

public class PenumbraService : IDisposable
{
    private const string SourceTag = "EmotePlugin";
    private const int PluginKey = -0x454D4F54; // "EMOT" as hex, negated for identification lock

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly GetEnabledState getEnabledState;
    private readonly GetModList getModList;
    private readonly GetCollections getCollections;
    private readonly GetCollection getCollection;
    private readonly GetCurrentModSettings getCurrentModSettings;
    private readonly SetTemporaryModSettings setTemporaryModSettings;
    private readonly RemoveTemporaryModSettings removeTemporaryModSettings;
    private readonly RemoveAllTemporaryModSettings removeAllTemporaryModSettings;
    private readonly OpenMainWindow openMainWindow;
    private readonly RedrawObject redrawObject;

    private readonly HashSet<Guid> usedCollections = new();

    public bool Available { get; private set; }

    public PenumbraService(IDalamudPluginInterface pi, IPluginLog log)
    {
        pluginInterface = pi;
        this.log = log;

        apiVersion = new ApiVersion(pi);
        getEnabledState = new GetEnabledState(pi);
        getModList = new GetModList(pi);
        getCollections = new GetCollections(pi);
        getCollection = new GetCollection(pi);
        getCurrentModSettings = new GetCurrentModSettings(pi);
        setTemporaryModSettings = new SetTemporaryModSettings(pi);
        removeTemporaryModSettings = new RemoveTemporaryModSettings(pi);
        removeAllTemporaryModSettings = new RemoveAllTemporaryModSettings(pi);
        openMainWindow = new OpenMainWindow(pi);
        redrawObject = new RedrawObject(pi);

        CheckAvailability();
    }

    public void CheckAvailability()
    {
        try
        {
            var version = apiVersion.Invoke();
            Available = version.Breaking >= 5;
        }
        catch
        {
            Available = false;
        }
    }

    public bool IsPenumbraEnabled()
    {
        if (!Available) return false;
        try
        {
            return getEnabledState.Invoke();
        }
        catch
        {
            return false;
        }
    }

    public Dictionary<string, string> GetMods()
    {
        if (!Available) return new Dictionary<string, string>();
        try
        {
            return getModList.Invoke();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get mod list: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    public Dictionary<Guid, string> GetCollectionList()
    {
        if (!Available) return new Dictionary<Guid, string>();
        try
        {
            return getCollections.Invoke();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get collections: {ex.Message}");
            return new Dictionary<Guid, string>();
        }
    }

    public void OpenModInPenumbra(string modDirectory, string modName = "")
    {
        if (!Available) return;
        try
        {
            openMainWindow.Invoke(TabType.Mods, modDirectory, modName);
        }
        catch (Exception ex)
        {
            log.Error($"Failed to open mod in Penumbra: {ex.Message}");
        }
    }

    public Guid GetCurrentCollectionId()
    {
        if (!Available) return Guid.Empty;
        try
        {
            var result = getCollection.Invoke(ApiCollectionType.Current);
            return result?.Id ?? Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    public bool ApplyTemporaryModSettings(string modDirectory, bool enabled, bool inherit, int priority,
        Guid collectionId, string modName = "", Dictionary<string, List<string>>? modSettings = null)
    {
        if (!Available) return false;
        try
        {
            var targetCollection = collectionId == Guid.Empty ? GetCurrentCollectionId() : collectionId;
            if (targetCollection == Guid.Empty) return false;

            IReadOnlyDictionary<string, IReadOnlyList<string>> settings;
            if (modSettings != null && modSettings.Count > 0)
            {
                settings = modSettings.ToDictionary(
                    k => k.Key,
                    k => (IReadOnlyList<string>)k.Value.ToList().AsReadOnly());
            }
            else
            {
                settings = new Dictionary<string, IReadOnlyList<string>>();
            }

            var result = setTemporaryModSettings.Invoke(
                targetCollection, modDirectory, inherit, enabled, priority,
                settings, SourceTag, PluginKey, modName);

            if (result == PenumbraApiEc.Success)
                usedCollections.Add(targetCollection);

            if (result != PenumbraApiEc.Success)
            {
                log.Warning($"Failed to set temp mod settings '{modDirectory}' enabled={enabled} priority={priority}: {result}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to set temp mod settings: {ex.Message}");
            return false;
        }
    }

    public bool RemoveTemporarySettings(string modDirectory, Guid collectionId, string modName = "")
    {
        if (!Available) return false;
        try
        {
            var targetCollection = collectionId == Guid.Empty ? GetCurrentCollectionId() : collectionId;
            if (targetCollection == Guid.Empty) return false;

            var result = removeTemporaryModSettings.Invoke(targetCollection, modDirectory, PluginKey, modName);
            if (result != PenumbraApiEc.Success)
            {
                log.Warning($"Failed to remove temp settings for '{modDirectory}': {result}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to remove temp settings: {ex.Message}");
            return false;
        }
    }

    public (bool Enabled, int Priority, Dictionary<string, List<string>> Settings) GetModSettings(
        string modDirectory, Guid collectionId, string modName = "")
    {
        if (!Available) return (false, 0, new());
        try
        {
            var targetCollection = collectionId == Guid.Empty ? GetCurrentCollectionId() : collectionId;
            if (targetCollection == Guid.Empty) return (false, 0, new());

            var (ec, settings) = getCurrentModSettings.Invoke(targetCollection, modDirectory, modName);
            if (ec != PenumbraApiEc.Success || settings == null) return (false, 0, new());

            // Item1=enabled, Item2=priority, Item3=option settings dict
            return (settings.Value.Item1, settings.Value.Item2, settings.Value.Item3);
        }
        catch
        {
            return (false, 0, new());
        }
    }

    public void RedrawSelf()
    {
        if (!Available) return;
        try
        {
            redrawObject.Invoke(0, RedrawType.Redraw); // 0 = local player
        }
        catch (Exception ex)
        {
            log.Error($"Failed to redraw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Clean up all temporary settings we applied across all collections
        try
        {
            if (Available)
            {
                foreach (var collectionId in usedCollections)
                {
                    try
                    {
                        removeAllTemporaryModSettings.Invoke(collectionId, PluginKey);
                    }
                    catch { }
                }
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
