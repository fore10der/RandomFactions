using System.Collections.Generic;
using Verse;

namespace RandomFactions;

public class RandomFactionsSettings : ModSettings
{
    public bool allowDuplicates = false;
    public bool removeOtherFactions = true;
    public bool verboseLogging = false;
    public int xenoPercent = 15;
    public List<string> userExcludedFactions = new List<string>();

    public override void ExposeData()
    {
        Scribe_Values.Look(ref removeOtherFactions, "removeOtherFactions", true);
        Scribe_Values.Look(ref allowDuplicates, "allowDuplicates", false);
        Scribe_Values.Look(ref xenoPercent, "xenoPercent", 15);
        Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
        Scribe_Collections.Look(ref userExcludedFactions, "userExcludedFactions", LookMode.Value);
    }
}
