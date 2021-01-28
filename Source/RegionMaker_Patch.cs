using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded
{
    class RegionMaker_Patch
    {

        public static FieldRef<RegionMaker, Map> mapFieldRef = FieldRefAccess<RegionMaker, Map>("map");
        public static FieldRef<RegionMaker, bool> workingFieldRef = FieldRefAccess<RegionMaker, bool>("working");
        public static FieldRef<RegionMaker, RegionGrid> regionGridFieldRef = FieldRefAccess<RegionMaker, RegionGrid>("regionGrid");
        public static FieldRef<RegionMaker, Region> newRegFieldRef = FieldRefAccess<RegionMaker, Region>("newReg");

        static MethodInfo methodFloodFillAndAddCells =
            Method(typeof(RegionMaker), "FloodFillAndAddCells", new Type[] { typeof(IntVec3) });
        static Action<RegionMaker, IntVec3> actionFloodFillAndAddCells =
            (Action<RegionMaker, IntVec3>)Delegate.CreateDelegate(typeof(Action<RegionMaker, IntVec3>), methodFloodFillAndAddCells);

        static MethodInfo methodCreateLinks =
            Method(typeof(RegionMaker), "CreateLinks", new Type[] { });
        static Action<RegionMaker> actionCreateLinks =
            (Action<RegionMaker>)Delegate.CreateDelegate(typeof(Action<RegionMaker>), methodCreateLinks);

        static MethodInfo methodRegisterThingsInRegionListers =
            Method(typeof(RegionMaker), "RegisterThingsInRegionListers", new Type[] { });
        static Action<RegionMaker> actionRegisterThingsInRegionListers =
            (Action<RegionMaker>)Delegate.CreateDelegate(typeof(Action<RegionMaker>), methodRegisterThingsInRegionListers);

        public static bool TryGenerateRegionFrom(RegionMaker __instance, ref Region __result, IntVec3 root)
        {
            RegionType expectedRegionType = root.GetExpectedRegionType(mapFieldRef(__instance));
            if (expectedRegionType == RegionType.None)
            {
                //Log.Warning("expectedRegionType == RegionType.None");
                __result = null;
                return false;
            }

            if (workingFieldRef(__instance))
            {
                Log.Error("Trying to generate a new region but we are currently generating one. Nested calls are not allowed.");
                __result = null;
                return false;
            }

            workingFieldRef(__instance) = true;
            try
            {
                regionGridFieldRef(__instance) = mapFieldRef(__instance).regionGrid;
                newRegFieldRef(__instance) = Region.MakeNewUnfilled(root, mapFieldRef(__instance));
                newRegFieldRef(__instance).type = expectedRegionType;
                if (newRegFieldRef(__instance).type == RegionType.Portal)
                {
                    newRegFieldRef(__instance).door = root.GetDoor(mapFieldRef(__instance));
                }

                actionFloodFillAndAddCells(__instance, root);
                actionCreateLinks(__instance);
                actionRegisterThingsInRegionListers(__instance);
                if (newRegFieldRef(__instance) == null)
                    Log.Warning("Trying to generate a new region but returned null");
                __result = newRegFieldRef(__instance);
                return false;
            }
            finally
            {
                workingFieldRef(__instance) = false;
            }
        }


    }
}
