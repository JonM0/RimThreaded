using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using System.Reflection;
using System.Threading;
using System.Collections.Concurrent;
using static HarmonyLib.AccessTools;

namespace RimThreaded
{

    public class RegionAndRoomUpdater_Patch
    {
        public static FieldRef<RegionAndRoomUpdater, Map> map =
            FieldRefAccess<RegionAndRoomUpdater, Map>("map");
        public static FieldRef<RegionAndRoomUpdater, List<Region>> newRegions =
            FieldRefAccess<RegionAndRoomUpdater, List<Region>>("newRegions");
        public static FieldRef<RegionAndRoomUpdater, List<Room>> newRooms =
            FieldRefAccess<RegionAndRoomUpdater, List<Room>>("newRooms");
        public static FieldRef<RegionAndRoomUpdater, HashSet<Room>> reusedOldRooms =
            FieldRefAccess<RegionAndRoomUpdater, HashSet<Room>>("reusedOldRooms");
        public static FieldRef<RegionAndRoomUpdater, List<RoomGroup>> newRoomGroups =
            FieldRefAccess<RegionAndRoomUpdater, List<RoomGroup>>("newRoomGroups");
        public static FieldRef<RegionAndRoomUpdater, HashSet<RoomGroup>> reusedOldRoomGroups =
            FieldRefAccess<RegionAndRoomUpdater, HashSet<RoomGroup>>("reusedOldRoomGroups");
        public static FieldRef<RegionAndRoomUpdater, List<Region>> currentRegionGroup =
            FieldRefAccess<RegionAndRoomUpdater, List<Region>>("currentRegionGroup");
        public static FieldRef<RegionAndRoomUpdater, List<Room>> currentRoomGroup =
            FieldRefAccess<RegionAndRoomUpdater, List<Room>>("currentRoomGroup");
        public static FieldRef<RegionAndRoomUpdater, bool> initialized =
            FieldRefAccess<RegionAndRoomUpdater, bool>("initialized");
        public static FieldRef<RegionAndRoomUpdater, bool> working =
            FieldRefAccess<RegionAndRoomUpdater, bool>("working");
        public static Dictionary<int, bool> threadRebuilding = new Dictionary<int, bool>();
        public static EventWaitHandle regionCleaning = new AutoResetEvent(true);

        static readonly MethodInfo methodShouldBeInTheSameRoomGroup =
            Method(typeof(RegionAndRoomUpdater), "ShouldBeInTheSameRoomGroup", new Type[] { typeof(Room), typeof(Room) });
        static readonly Func<RegionAndRoomUpdater, Room, Room, bool> funcShouldBeInTheSameRoomGroup =
            (Func<RegionAndRoomUpdater, Room, Room, bool>)Delegate.CreateDelegate(typeof(Func<RegionAndRoomUpdater, Room, Room, bool>), methodShouldBeInTheSameRoomGroup);


        public static bool TryRebuildDirtyRegionsAndRooms(RegionAndRoomUpdater __instance)
        {
            if (!__instance.Enabled || getThreadRebuilding()) return false;
            regionCleaning.WaitOne();
            setThreadRebuilding(true);
            if (!initialized(__instance)) __instance.RebuildAllRegionsAndRooms();
            if (RegionDirtyer_Patch.get_DirtyCells(map(__instance).regionDirtyer).IsEmpty)
            {
                resumeThreads();
                return false;
            }
            List<Region> localNewRegions = new List<Region>();
            try
            {
                RegenerateNewRegionsFromDirtyCells2(__instance, localNewRegions);
                CreateOrUpdateRooms2(__instance);
            }
            catch (Exception arg) { Log.Error("Exception while rebuilding dirty regions: " + arg); }
            newRegions(__instance).Clear();
            RegionDirtyer_Patch.SetAllClean2(map(__instance).regionDirtyer);
            initialized(__instance) = true;
            resumeThreads();
            if (DebugSettings.detectRegionListersBugs) Autotests_RegionListers.CheckBugs(map(__instance));
            return false;
        }

        private static void RegenerateNewRegionsFromDirtyCells2(RegionAndRoomUpdater __instance, List<Region> localNewRegions)
        {
            //newRegions(__instance).Clear(); //already cleared at end of method TryRebuildDirtyRegionsAndRooms()
            Map localMap = map(__instance);
            RegionDirtyer regionDirtyer = localMap.regionDirtyer;
            ConcurrentQueue<IntVec3> dirtyCells = RegionDirtyer_Patch.get_DirtyCells(regionDirtyer);
            while (dirtyCells.TryDequeue(out IntVec3 dirtyCell))
            {
                if (dirtyCell.GetRegion(localMap, RegionType.Set_All) == null)
                {
                    Region region = localMap.regionMaker.TryGenerateRegionFrom(dirtyCell);
                    if (region != null)
                    {
                        localNewRegions.Add(region);
                    }
                }
                localMap.temperatureCache.ResetCachedCellInfo(dirtyCell);
            }
        }

        private static void CreateOrUpdateRooms2(RegionAndRoomUpdater __instance)
        {
            newRooms(__instance).Clear();
            reusedOldRooms(__instance).Clear();
            newRoomGroups(__instance).Clear();
            reusedOldRoomGroups(__instance).Clear();
            int numRegionGroups = CombineNewRegionsIntoContiguousGroups2(__instance);
            CreateOrAttachToExistingRooms2(__instance, numRegionGroups);
            int numRoomGroups = CombineNewAndReusedRoomsIntoContiguousGroups2(__instance);
            CreateOrAttachToExistingRoomGroups2(__instance, numRoomGroups);
            NotifyAffectedRoomsAndRoomGroupsAndUpdateTemperature2(__instance);
        }
        private static int CombineNewRegionsIntoContiguousGroups2(RegionAndRoomUpdater __instance)
        {
            int num = 0;
            for (int i = 0; i < newRegions(__instance).Count; i++)
            {
                if (newRegions(__instance)[i].newRegionGroupIndex < 0)
                {
                    RegionTraverser.FloodAndSetNewRegionIndex(newRegions(__instance)[i], num);
                    num++;
                }
            }
            return num;
        }
        private static void CreateOrAttachToExistingRooms2(RegionAndRoomUpdater __instance, int numRegionGroups)
        {
            for (int i = 0; i < numRegionGroups; i++)
            {
                currentRegionGroup(__instance).Clear();
                for (int j = 0; j < newRegions(__instance).Count; j++)
                {
                    if (newRegions(__instance)[j].newRegionGroupIndex == i)
                    {
                        currentRegionGroup(__instance).Add(newRegions(__instance)[j]);
                    }
                }
                if (!currentRegionGroup(__instance)[0].type.AllowsMultipleRegionsPerRoom())
                {
                    if (currentRegionGroup(__instance).Count != 1)
                    {
                        Log.Error("Region type doesn't allow multiple regions per room but there are >1 regions in this group.");
                    }

                    Room room = Room.MakeNew(map(__instance));
                    currentRegionGroup(__instance)[0].Room = room;
                    newRooms(__instance).Add(room);
                    continue;
                }
                Room room2 = FindCurrentRegionGroupNeighborWithMostRegions2(__instance, out bool multipleOldNeighborRooms);
                if (room2 == null)
                {
                    Room item = RegionTraverser.FloodAndSetRooms(currentRegionGroup(__instance)[0], map(__instance), null);
                    newRooms(__instance).Add(item);
                }
                else if (!multipleOldNeighborRooms)
                {
                    for (int k = 0; k < currentRegionGroup(__instance).Count; k++)
                    {
                        currentRegionGroup(__instance)[k].Room = room2;
                    }
                    reusedOldRooms(__instance).Add(room2);
                }
                else
                {
                    RegionTraverser.FloodAndSetRooms(currentRegionGroup(__instance)[0], map(__instance), room2);
                    reusedOldRooms(__instance).Add(room2);
                }
            }
        }

        private static int CombineNewAndReusedRoomsIntoContiguousGroups2(RegionAndRoomUpdater __instance)
        {
            int num = 0;
            foreach (Room reusedOldRoom in reusedOldRooms(__instance))
            {
                reusedOldRoom.newOrReusedRoomGroupIndex = -1;
            }
            Stack<Room> tmpRoomStack = new Stack<Room>();
            foreach (Room item in reusedOldRooms(__instance).Concat(newRooms(__instance)))
            {
                if (item.newOrReusedRoomGroupIndex < 0)
                {
                    tmpRoomStack.Clear();
                    tmpRoomStack.Push(item);
                    item.newOrReusedRoomGroupIndex = num;
                    while (tmpRoomStack.Count != 0)
                    {
                        Room room = tmpRoomStack.Pop();
                        foreach (Room neighbor in room.Neighbors)
                        {
                            if (neighbor.newOrReusedRoomGroupIndex < 0 && funcShouldBeInTheSameRoomGroup(__instance, room, neighbor))
                            {
                                neighbor.newOrReusedRoomGroupIndex = num;
                                tmpRoomStack.Push(neighbor);
                            }
                        }
                    }
                    num++;
                }
            }
            return num;
        }

        private static void CreateOrAttachToExistingRoomGroups2(RegionAndRoomUpdater __instance, int numRoomGroups)
        {
            for (int i = 0; i < numRoomGroups; i++)
            {
                currentRoomGroup(__instance).Clear();
                foreach (Room reusedOldRoom in reusedOldRooms(__instance))
                {
                    if (reusedOldRoom.newOrReusedRoomGroupIndex == i)
                    {
                        currentRoomGroup(__instance).Add(reusedOldRoom);
                    }
                }
                for (int j = 0; j < newRooms(__instance).Count; j++)
                {
                    if (newRooms(__instance)[j].newOrReusedRoomGroupIndex == i)
                    {
                        currentRoomGroup(__instance).Add(newRooms(__instance)[j]);
                    }
                }
                bool multipleOldNeighborRoomGroups;
                RoomGroup roomGroup = FindCurrentRoomGroupNeighborWithMostRegions2(__instance, out multipleOldNeighborRoomGroups);
                if (roomGroup == null)
                {
                    RoomGroup roomGroup2 = RoomGroup.MakeNew(map(__instance));
                    FloodAndSetRoomGroups2(__instance, currentRoomGroup(__instance)[0], roomGroup2);
                    newRoomGroups(__instance).Add(roomGroup2);
                }
                else if (!multipleOldNeighborRoomGroups)
                {
                    for (int k = 0; k < currentRoomGroup(__instance).Count; k++)
                    {
                        currentRoomGroup(__instance)[k].Group = roomGroup;
                    }

                    reusedOldRoomGroups(__instance).Add(roomGroup);
                }
                else
                {
                    FloodAndSetRoomGroups2(__instance, currentRoomGroup(__instance)[0], roomGroup);
                    reusedOldRoomGroups(__instance).Add(roomGroup);
                }
            }
        }
        private static RoomGroup FindCurrentRoomGroupNeighborWithMostRegions2(RegionAndRoomUpdater __instance, out bool multipleOldNeighborRoomGroups)
        {
            multipleOldNeighborRoomGroups = false;
            RoomGroup roomGroup = null;
            for (int i = 0; i < currentRoomGroup(__instance).Count; i++)
            {
                foreach (Room neighbor in currentRoomGroup(__instance)[i].Neighbors)
                {
                    if (neighbor.Group != null && funcShouldBeInTheSameRoomGroup(__instance, currentRoomGroup(__instance)[i], neighbor) && !reusedOldRoomGroups(__instance).Contains(neighbor.Group))
                    {
                        if (roomGroup == null)
                        {
                            roomGroup = neighbor.Group;
                        }
                        else if (neighbor.Group != roomGroup)
                        {
                            multipleOldNeighborRoomGroups = true;
                            if (neighbor.Group.RegionCount > roomGroup.RegionCount)
                            {
                                roomGroup = neighbor.Group;
                            }
                        }
                    }
                }
            }
            return roomGroup;
        }
        private static void NotifyAffectedRoomsAndRoomGroupsAndUpdateTemperature2(RegionAndRoomUpdater __instance)
        {
            foreach (Room reusedOldRoom in reusedOldRooms(__instance))
            {
                reusedOldRoom.Notify_RoomShapeOrContainedBedsChanged();
            }
            for (int i = 0; i < newRooms(__instance).Count; i++)
            {
                newRooms(__instance)[i].Notify_RoomShapeOrContainedBedsChanged();
            }
            foreach (RoomGroup reusedOldRoomGroup in reusedOldRoomGroups(__instance))
            {
                reusedOldRoomGroup.Notify_RoomGroupShapeChanged();
            }
            for (int j = 0; j < newRoomGroups(__instance).Count; j++)
            {
                RoomGroup roomGroup = newRoomGroups(__instance)[j];
                roomGroup.Notify_RoomGroupShapeChanged();
                if (map(__instance).temperatureCache.TryGetAverageCachedRoomGroupTemp(roomGroup, out float result))
                {
                    roomGroup.Temperature = result;
                }
            }
        }

        private static void FloodAndSetRoomGroups2(RegionAndRoomUpdater __instance, Room start, RoomGroup roomGroup)
        {
            Stack<Room> tmpRoomStack = new Stack<Room>();
            tmpRoomStack.Push(start);
            HashSet<Room> tmpVisitedRooms = new HashSet<Room>();
            tmpVisitedRooms.Add(start);
            while (tmpRoomStack.Count != 0)
            {
                Room room = tmpRoomStack.Pop();
                room.Group = roomGroup;
                foreach (Room neighbor in room.Neighbors)
                {
                    if (!tmpVisitedRooms.Contains(neighbor) && funcShouldBeInTheSameRoomGroup(__instance, room, neighbor))
                    {
                        tmpRoomStack.Push(neighbor);
                        tmpVisitedRooms.Add(neighbor);
                    }
                }
            }
        }

        private static Room FindCurrentRegionGroupNeighborWithMostRegions2(RegionAndRoomUpdater __instance, out bool multipleOldNeighborRooms)
        {
            multipleOldNeighborRooms = false;
            Room room = null;
            for (int i = 0; i < currentRegionGroup(__instance).Count; i++)
            {
                foreach (Region item in currentRegionGroup(__instance)[i].NeighborsOfSameType)
                {
                    if (item.Room != null && !reusedOldRooms(__instance).Contains(item.Room))
                    {
                        if (room == null)
                        {
                            room = item.Room;
                        }
                        else if (item.Room != room)
                        {
                            multipleOldNeighborRooms = true;
                            if (item.Room.RegionCount > room.RegionCount)
                            {
                                room = item.Room;
                            }
                        }
                    }
                }
            }
            return room;
        }



        private static void resumeThreads()
        {
            regionCleaning.Set();
            setThreadRebuilding(false);
        }

        private static void setThreadRebuilding(bool v)
        {
            int tID = Thread.CurrentThread.ManagedThreadId;
            lock (threadRebuilding)
            {
                threadRebuilding[tID] = v;
            }
        }

        private static bool getThreadRebuilding()
        {
            int tID = Thread.CurrentThread.ManagedThreadId;
            if (!threadRebuilding.TryGetValue(tID, out bool rebuilding))
            {
                rebuilding = false;
                lock (threadRebuilding)
                {
                    threadRebuilding[tID] = rebuilding;
                }
            }
            return rebuilding;
        }


    }
}
