using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded
{
    public class RegionGrid_Patch
    {

        public static FieldRef<RegionGrid, Map> mapFieldRef = FieldRefAccess<RegionGrid, Map>("map");
        public static FieldRef<RegionGrid, Region[]> regionGridFieldRef = FieldRefAccess<RegionGrid, Region[]>("regionGrid");
        public static void SetRegionAt(IntVec3 c, Region reg)
        {
            if (reg == null) 
                Log.Warning("Setting Region to null");
        }

        public static bool GetValidRegionAt(RegionGrid __instance, ref Region __result, IntVec3 c)
        {
            if (!c.InBounds(mapFieldRef(__instance)))
            {
                Log.Error("Tried to get valid region out of bounds at " + c);
                __result = null;
                return false;
            }

            if (!mapFieldRef(__instance).regionAndRoomUpdater.Enabled && mapFieldRef(__instance).regionAndRoomUpdater.AnythingToRebuild)
            {
                Log.Warning(string.Concat("Trying to get valid region at ", c, " but RegionAndRoomUpdater is disabled. The result may be incorrect."));
            }

            mapFieldRef(__instance).regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Region region = regionGridFieldRef(__instance)[mapFieldRef(__instance).cellIndices.CellToIndex(c)];
            if (region != null && region.valid)
            {
                __result = region;
                return false;
            }

            __result = null;
            return false;
        }

    }
}
