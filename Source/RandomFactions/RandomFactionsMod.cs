using Mlie;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;

namespace RandomFactions;

public class RandomFactionsMod : Mod
{
    public const string RandomCategoryName = "Random";
    public const string XenopatchCategoryName = "Xenopatch";

    // exclude specific xenotypes from specific FactionDefs
    private static readonly Dictionary<string, HashSet<string>> BlockedPairs = new()
    {
        { "OutlanderRough", new HashSet<string> { "Pigskin" } },
        { "OutlanderCivil", new HashSet<string> { "Papago", "Hunterphage" } },
        { "Pirate", new HashSet<string> { "Waster", "Yttakin" } },
        { "TribeSavage", new HashSet<string> { "Impid", "Starjack" } },
        { "TribeRough", new HashSet<string> { "Neanderthal", "Starjack" } },
        { "TribeCivil", new HashSet<string> { "Starjack" } },
        { "TribeCannibal", new HashSet<string> { "Starjack" } },
        { "NudistTribe", new HashSet<string> { "Starjack" } }
    };
    private static string currentVersion;

    private static readonly Dictionary<(string baseFactionDefName, string xenotype), string> XenotypeFactionOverrides =
 new()
    {
        { ("TribeRough",      "Neanderthal"), "TribeRoughNeanderthal" },
        { ("Pirate",          "Yttakin"), "PirateYttakin" },
        { ("TribeSavage",     "Impid"), "TribeSavageImpid" },
        { ("OutlanderRough",  "Pigskin"), "OutlanderRoughPig" },
        { ("PirateRough",     "Waster"), "PirateWaster" },
        { ("Sanguophage",     "Sanguophage"), "Sanguophages" },
        { ("OutlanderCivil",  "Papago"), "OutlanderPapou" },
        { ("OutlanderCivil",  "Hunterphage"), "HuntersCovenant" },
    };

    // Xenotype faction creation - now with filtering
    // Easy to extend to allow mod options to blacklist, etc.

    // Xenotypes that should NEVER be used to create new xenotype factions
    public static readonly HashSet<string> GloballyBlockedXenotypes = new()
    {
        "Baseliner", // handled by the base Defs
        "Highmate"
    };

    // don't make xenotype factions with these, leads to nonsensical "waster savage impid tribe", etc.
    private static readonly HashSet<string> DefaultExcludedFactionDefs = new()
    {
        "TribeRoughNeanderthal",
        "PirateYttakin",
        "TribeSavageImpid",
        "OutlanderRoughPig",
        "PirateWaster",
        "Sanguophages",
        "TradersGuild",
        "Empire",
        //modded regular faction
        "EVA_Faction",
        //modded xenotype faction (this is really going to need to become a mod option)
        "HuntersCovenant",// Hunterphage
        "OutlanderPapou" // Papou
    };

    private static HashSet<string> cachedExcludedFactionDefs;
    public static HashSet<string> HardExcludedFactionDefs
    {
        get
        {
            if (cachedExcludedFactionDefs == null)
            {
                RebuildExcludedFactionDefs();
            }
            return cachedExcludedFactionDefs;
        }
    }

    public static void RebuildExcludedFactionDefs()
    {
        cachedExcludedFactionDefs = new HashSet<string>(DefaultExcludedFactionDefs);
        if (SettingsInstance?.userExcludedFactions != null)
        {
            cachedExcludedFactionDefs.UnionWith(SettingsInstance.userExcludedFactions);
        }
    }

    public static RandomFactionsMod Instance;

    public static RandomFactionsSettings SettingsInstance;
    private readonly Dictionary<FactionDef, int> randCountRecord = new();
    private readonly Dictionary<FactionDef, int> zeroCountRecord = new();

    public readonly ModLogger Logger;

    public readonly Dictionary<string, FactionDef> patchedXenotypeFactions = new();

    private Vector2 scrollPosition;
    private float lastViewHeight = 1000f;

    public RandomFactionsMod(ModContentPack content) : base(content)
    {
        Instance = this;
        currentVersion = VersionFromManifest.GetVersionFromModMetaData(content.ModMetaData);
        Logger = new ModLogger("RandomFactionsMod");
        SettingsInstance = GetSettings<RandomFactionsSettings>();
        
        // Initialize list if null
        if (SettingsInstance.userExcludedFactions == null)
        {
            SettingsInstance.userExcludedFactions = new List<string>();
        }
        
        // Ensure the cache is built on startup
        RebuildExcludedFactionDefs();

        Logger.Trace("RandomFactionsMod constructed");

        // Run DefsLoaded logic asynchronously so all defs are available
        LongEventHandler.QueueLongEvent(DefsLoaded, "RandomFactions:LoadingDefs", false, null);
    }

    //public static string XenoFactionDefName(XenotypeDef xdef, FactionDef fdef)
    //{
    // Unique name by concatenating the xenotype name and the faction name
    //    if (xdef == null) throw new ArgumentNullException(nameof(xdef));
    //    if (fdef == null) throw new ArgumentNullException(nameof(fdef));

    //    return $"{xdef.defName}_{fdef.defName}";
    //}


    private static string BaseFactionKey(FactionDef def)
    {
        // Use exact defName for key, not substring
        return def.defName;
    }


    private static FactionDef cloneDef(FactionDef def)
    {
        var cpy = new FactionDef();
        reflectionCopy(def, cpy);
        cpy.debugRandomId = (ushort)(def.debugRandomId + 1);
        return cpy;
    }

    private void createXenoFactions()
    {
        Logger.Trace("Starting xenotype faction creation...");

        var newDefs = new List<FactionDef>();
        var violenceCapableXenotypes = getViolenceCapableXenotypes();

        Logger.Trace($"Found {violenceCapableXenotypes.Count} violence-capable xenotypes.");

        foreach (var def in DefDatabase<FactionDef>.AllDefs)
        {
            if (HardExcludedFactionDefs.Contains(def.defName))
            {
                Logger.Trace($"Skipping hard-excluded faction: {def.defName}");
                continue;
            }

            if (!IsXenotypePatchable(def))
            {
                Logger.Trace(
                    $"Skipping non-patchable faction: {def.defName} (hidden={def.hidden}, maxConfig={def.maxConfigurableAtWorldCreation}, isPlayer={def.isPlayer})");
                continue;
            }

            Logger.Trace($"Processing patchable faction: {def.defName}");

            foreach (var xenotypeDef in violenceCapableXenotypes)
            {
                // GLOBAL xenotype exclusion
                if (GloballyBlockedXenotypes.Contains(xenotypeDef.defName))
                {
                    Logger.Trace($" - Globally blocked xenotype '{xenotypeDef.defName}' — skipping.");
                    continue;
                }

                // PAIR exclusion (e.g., OutlanderRough → no Pigskin)
                if (BlockedPairs.TryGetValue(def.defName, out var blockedXenos))
                {
                    if (blockedXenos.Contains(xenotypeDef.defName))
                    {
                        Logger.Trace(
                            $" - Blocked pair: faction '{def.defName}' cannot use xenotype '{xenotypeDef.defName}'");
                        continue;
                    }
                }

                Logger.Trace($" - Applying xenotype: {xenotypeDef.defName}");

                // *** NEW LOGIC: Determine final defName before cloning ***
                string newName = XenoFactionDefName(xenotypeDef, def);

                // null means: override exists → skip generation
                if (newName == null)
                {
                    Logger.Trace(
                        $"   - Skipping creation: existing overridden faction already defined for ({xenotypeDef.defName} + {def.defName})");
                    continue;
                }

                // Check for duplicates early (safety)
                if (DefDatabase<FactionDef>.GetNamedSilentFail(newName) != null)
                {
                    Logger.Warning($"   - WARNING: Attempted to create duplicate faction def '{newName}'. Skipping.");
                    continue;
                }

                // Clone & apply
                var defCopy = cloneDef(def);
                defCopy.defName = newName;
                defCopy.categoryTag = XenopatchCategoryName;
                defCopy.label = $"{xenotypeDef.label} {defCopy.label}";

                // Create XenotypeSet
                var xenoChance = new XenotypeChance(xenotypeDef, 1f);
                var xenotypeChances = new List<XenotypeChance> { xenoChance };
                var newXenoSet = new XenotypeSet();

                foreach (var field in typeof(XenotypeSet).GetFields(
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                {
                    if (field.FieldType.IsAssignableFrom(xenotypeChances.GetType()))
                    {
                        field.SetValue(newXenoSet, xenotypeChances);
                        Logger.Trace($"   - Set XenotypeSet field '{field.Name}' for {defCopy.defName}");
                    }
                }

                defCopy.xenotypeSet = newXenoSet;
                defCopy.maxConfigurableAtWorldCreation = 0;
                defCopy.hidden = true;

                Logger.Trace($"   - Created xenotype faction def: {defCopy.defName}");

                newDefs.Add(defCopy);
            }
        }

        // Add to database
        foreach (var def in newDefs)
        {
            patchedXenotypeFactions[def.defName] = def;
            DefDatabase<FactionDef>.Add(def);
        }

        Logger.Trace($"Created {newDefs.Count} xenotype faction defs (after filtering).");
    }

    private void DefsLoaded()
    {
        Logger.Trace("DefsLoaded: initializing settings and generating xeno factions");

        if (SettingsInstance.removeOtherFactions)
        {
            zeroCountFactionDefs();
        }

        if (ModsConfig.BiotechActive)
        {
            createXenoFactions();
        }

        Logger.Trace("DefsLoaded complete");
    }


    private static List<XenotypeDef> getViolenceCapableXenotypes()
    {
        return DefDatabase<XenotypeDef>.AllDefs
            .Where(
                x =>
                {
                    if (x.genes == null)
                    {
                        return true;
                    }

                    var combinedDisabled = WorkTags.None;
                    foreach (var gene in x.genes)
                    {
                        combinedDisabled |= gene.disabledWorkTags;
                    }

                    return (combinedDisabled & WorkTags.Violent) == 0;
                })
            .ToList();
    }

    private static void reflectionCopy(object a, object b)
    {
        foreach (var field in a.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            field.SetValue(b, field.GetValue(a));
        }
    }

    private void SettingsChanged()
    {
        RebuildExcludedFactionDefs();
        if (SettingsInstance.removeOtherFactions)
        {
            zeroCountFactionDefs();
        }
        else
        {
            undoZeroCountFactionDefs();
        }
    }

    private void undoZeroCountFactionDefs()
    {
        foreach (var def in zeroCountRecord.Keys)
        {
            def.startingCountAtWorldCreation = zeroCountRecord[def];
        }

        foreach (var def in DefDatabase<FactionDef>.AllDefs)
        {
            if (RandomCategoryName.EqualsIgnoreCase(def.categoryTag))
            {
                randCountRecord[def] = def.startingCountAtWorldCreation;
                def.startingCountAtWorldCreation = 0;
            }
        }
    }

    private void zeroCountFactionDefs()
    {
        foreach (var def in DefDatabase<FactionDef>.AllDefs)
        {
            if (def.hidden ||
                def.isPlayer ||
                RandomCategoryName.EqualsIgnoreCase(def.categoryTag) ||
                "Empire".EqualsIgnoreCase(def.defName))
            {
                continue;
            }

            zeroCountRecord[def] = def.startingCountAtWorldCreation;
            def.startingCountAtWorldCreation = 0;
        }

        foreach (var def in randCountRecord.Keys)
        {
            def.startingCountAtWorldCreation = randCountRecord[def];
        }
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        // Ensure SettingsInstance is valid
        if (SettingsInstance == null)
        {
             SettingsInstance = GetSettings<RandomFactionsSettings>();
        }

        // Ensure user list is initialized
        if (SettingsInstance.userExcludedFactions == null)
        {
            SettingsInstance.userExcludedFactions = new List<string>();
        }

        Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, lastViewHeight);
        Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

        Listing_Standard listing = new Listing_Standard();
        listing.Begin(viewRect);

        listing.CheckboxLabeled(
            "RaFa.reorganiseFactions".Translate(),
            ref SettingsInstance.removeOtherFactions,
            "RaFa.reorganiseFactionsTT".Translate());

        listing.CheckboxLabeled(
            "RaFa.allowDuplicates".Translate(),
            ref SettingsInstance.allowDuplicates,
            "RaFa.allowDuplicatesTT".Translate());

        listing.Label("RaFa.xenotypePercent".Translate() + ": " + SettingsInstance.xenoPercent);
        SettingsInstance.xenoPercent = (int)listing.Slider(SettingsInstance.xenoPercent, 0f, 100f);

        listing.CheckboxLabeled(
            "RaFa.verboseLogging".Translate(),
            ref SettingsInstance.verboseLogging,
            "RaFa.verboseLoggingTT".Translate());

        if (currentVersion != null)
        {
            listing.Gap();
            GUI.contentColor = Color.gray;
            listing.Label("RaFa.currentModVersion".Translate(currentVersion));
            GUI.contentColor = Color.white;
        }

        listing.Gap();
        listing.GapLine();
        
        // Draw the excluded factions inline
        DrawExcludedFactionsList(listing);

        listing.End();
        
        // Recalculate the content height for the next frame
        // IMPORTANT: We must ensure it's at least as big as the window to avoid scrolling if not needed
        // but large enough if content expands
        lastViewHeight = Math.Max(listing.CurHeight + 100f, inRect.height);
        
        Widgets.EndScrollView();

        base.DoSettingsWindowContents(inRect);
    }

    private void DrawExcludedFactionsList(Listing_Standard listing)
    {
        listing.Label("Excluded Factions (Requires game restart to apply)");
        
        bool changed = false;

        // Double check for null before accessing Count
        if (SettingsInstance.userExcludedFactions == null)
        {
            SettingsInstance.userExcludedFactions = new List<string>();
        }

        if (SettingsInstance.userExcludedFactions.Count > 0)
        {
            for (int i = 0; i < SettingsInstance.userExcludedFactions.Count; i++)
            {
                string defName = SettingsInstance.userExcludedFactions[i];
                FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(defName);
                
                Rect rowRect = listing.GetRect(32f);
                
                // Draw background for alternate rows
                if (i % 2 == 1) Widgets.DrawLightHighlight(rowRect);

                if (def != null)
                {
                    // Info Card
                    Rect infoRect = new Rect(rowRect.x, rowRect.y + 4f, 24f, 24f);
                    Widgets.InfoCardButton(infoRect.x, infoRect.y, def);

                    // Icon
                    Rect iconRect = new Rect(infoRect.xMax + 4f, rowRect.y + 4f, 24f, 24f);
                    try
                    {
                        GUI.color = def.DefaultColor;
                        if (def.FactionIcon != null)
                        {
                            GUI.DrawTexture(iconRect, def.FactionIcon);
                        }
                        GUI.color = Color.white;
                    }
                    catch
                    {
                        // Handle missing textures gracefully
                    }

                    // Label
                    Rect labelRect = new Rect(iconRect.xMax + 8f, rowRect.y, rowRect.width - 120f, 32f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(labelRect, def.LabelCap);
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                else
                {
                    // Fallback for removed mods/missing defs
                    Rect labelRect = new Rect(rowRect.x + 36f, rowRect.y, rowRect.width - 120f, 32f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = Color.red;
                    Widgets.Label(labelRect, defName + " (Missing)");
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                }

                // Remove Button
                Rect btnRect = new Rect(rowRect.width - 80f, rowRect.y + 4f, 70f, 24f);
                if (Widgets.ButtonText(btnRect, "Remove"))
                {
                    SettingsInstance.userExcludedFactions.RemoveAt(i);
                    changed = true;
                    i--;
                }
                
                listing.Gap(2f);
            }
        }
        else
        {
            listing.Label("No user excluded factions.");
        }

        listing.Gap(10f);

        // Add Button
        if (listing.ButtonText("Add..."))
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            var candidates = DefDatabase<FactionDef>.AllDefsListForReading
                .Where(f => !f.isPlayer && !f.hidden && f.categoryTag != RandomCategoryName && f.categoryTag != XenopatchCategoryName)
                .OrderBy(f => f.label);

            foreach (var f in candidates)
            {
                if (SettingsInstance.userExcludedFactions.Contains(f.defName) || DefaultExcludedFactionDefs.Contains(f.defName))
                    continue;

                options.Add(new FloatMenuOption(f.LabelCap, () =>
                {
                    SettingsInstance.userExcludedFactions.Add(f.defName);
                    RebuildExcludedFactionDefs();
                }, f.FactionIcon, f.DefaultColor));
            }

            if (options.Count == 0) options.Add(new FloatMenuOption("No other factions available", null));

            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (changed) RebuildExcludedFactionDefs();
    }

    public static bool IsXenotypePatchable(FactionDef def)
    {
        // NEVER patch xenotype-only defs (vanilla or modded)
        if (RandomFactionGenerator.XenotypeOnlyFactionDefNames.Contains(def.defName))
        {
            return false;
        }

        // Don’t patch player or hidden defs
        if (def.isPlayer || def.hidden)
        {
            return false;
        }

        // Don’t patch factions already generated by this mod
        if (RandomCategoryName.EqualsIgnoreCase(def.categoryTag))
        {
            return false;
        }

        // Otherwise OK
        return true;
    }

    public override string SettingsCategory() { return "RaFa.ModName".Translate(); }

    public override void WriteSettings()
    {
        base.WriteSettings();
        SettingsChanged();
    }

    public static string XenoFactionDefName(XenotypeDef xdef, FactionDef fdef)
    {
        if (xdef == null)
        {
            throw new ArgumentNullException(nameof(xdef));
        }

        if (fdef == null)
        {
            throw new ArgumentNullException(nameof(fdef));
        }

        string xenotype = xdef.defName;
        string baseKey = BaseFactionKey(fdef);

        // Check for override-based canonical factions
        if (XenotypeFactionOverrides.TryGetValue((baseKey, xenotype), out string mapped))
        {
            // If the mapped def exists already — DO NOT CREATE A DUPLICATE
            if (DefDatabase<FactionDef>.GetNamedSilentFail(mapped) != null)
            {
                // Signal caller: "skip creation"
                return null;
            }

            return mapped;
        }

        // Default generated name
        return $"{xenotype}_{fdef.defName}";
    }
}
