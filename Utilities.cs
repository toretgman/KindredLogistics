﻿using Il2CppInterop.Runtime;
using KindredLogistics.Patches;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Scripting;
using Stunlock.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics
{
    public static class Utilities
    {
        public static readonly ComponentType[] StashQuery =
            [
                ComponentType.ReadOnly(Il2CppType.Of<InventoryOwner>()),
                ComponentType.ReadOnly(Il2CppType.Of<CastleHeartConnection>()),
                ComponentType.ReadOnly(Il2CppType.Of<AttachedBuffer>()),
                ComponentType.ReadOnly(Il2CppType.Of<NameableInteractable>()),
            ];

        public static readonly ComponentType[] RefinementStationQuery =
            [
                ComponentType.ReadOnly(Il2CppType.Of<Team>()),
                ComponentType.ReadOnly(Il2CppType.Of<CastleHeartConnection>()),
                ComponentType.ReadOnly(Il2CppType.Of<Refinementstation>()),
                ComponentType.ReadOnly(Il2CppType.Of<NameableInteractable>()),
            ];

        public static readonly ComponentType[] UserEntityQuery =
        [
                ComponentType.ReadOnly(Il2CppType.Of<User>()),
        ];

        public static void StashServantInventory(Entity servant)
        {
            var serverGameManager = Core.ServerGameManager;
            var matches = new NativeHashMap<PrefabGUID, List<(Entity stash, Entity inventory)>>(100, Allocator.TempJob);
            (Entity stash, Entity inventory) missionStash = (Entity.Null, Entity.Null);
            try
            {
                foreach (Entity stash in StashService.GetAllAlliedStashesOnTerritory(servant))
                {
                    if (stash.Read<NameableInteractable>().Name.ToString().ToLower().Contains("spoils") && missionStash.stash.Equals(Entity.Null)) // store mission stash for later
                    {
                        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, stash, out Entity missionInventory)) continue;
                        missionStash = (stash, missionInventory);
                        break;
                    }
                    if (!serverGameManager.TryGetBuffer<AttachedBuffer>(stash, out var buffer))
                        continue;

                    foreach (var attachedBuffer in buffer)
                    {
                        Entity attachedEntity = attachedBuffer.Entity;
                        if (!attachedEntity.Has<PrefabGUID>()) continue;
                        if (!attachedEntity.Read<PrefabGUID>().Equals(StashServices.ExternalInventoryPrefab)) continue;

                        var checkInventoryBuffer = attachedEntity.ReadBuffer<InventoryBuffer>();
                        foreach (var inventoryEntry in checkInventoryBuffer)
                        {
                            var item = inventoryEntry.ItemType;
                            if (item.GuidHash == 0) continue;
                            if (!matches.TryGetValue(item, out var itemMatches))
                            {
                                itemMatches = new List<(Entity stash, Entity inventory)>();
                                matches[item] = itemMatches;
                            }
                            else if (itemMatches.Any(x => x.stash == stash)) continue;
                            itemMatches.Add((stash, attachedEntity));
                        }
                    }
                }
                if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, servant, out Entity inventory))
                    return;

                if (!serverGameManager.TryGetBuffer<InventoryBuffer>(inventory, out var inventoryBuffer))
                    return;
                for (int i = 0; i < inventoryBuffer.Length; i++)
                {
                    var item = inventoryBuffer[i].ItemType;
                    if (!matches.TryGetValue(item, out var stashEntries) && !missionStash.stash.Equals(Entity.Null))
                    {
                        int transferAmount = serverGameManager.GetInventoryItemCount(inventory, item);
                        TransferItems(serverGameManager, inventory, missionStash.inventory, item, transferAmount);
                        continue;
                    }

                    foreach (var stashEntry in stashEntries)
                    {
                        int transferAmount = serverGameManager.GetInventoryItemCount(inventory, item);
                        TransferItems(serverGameManager, inventory, stashEntry.inventory, item, transferAmount);
                    }
                }
            }
            catch (Exception e)
            {
                Core.Log.LogError($"Exited StashServantInventory early: {e}");
            }
            finally
            {
                matches.Dispose();
            }
        }

        public static bool TerritoryCheck(Entity character, Entity target)
        {
            if (!target.Has<CastleHeartConnection>())
                return false;

            var charPos = character.Read<TilePosition>();
            var heart = target.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();
            var castleHeart = heart.Read<CastleHeart>();
            var castleTerritory = castleHeart.CastleTerritoryEntity;
            return CastleTerritoryExtensions.IsTileInTerritory(Core.EntityManager, charPos.Tile, ref castleTerritory, out var _);
        }

        public static bool SharedHeartConnection(Entity input, Entity ouput)
        {
            if (input.Has<CastleHeartConnection>() && ouput.Has<CastleHeartConnection>())
            {
                var inputHeart = input.Read<CastleHeartConnection>().CastleHeartEntity._Entity;
                var outputHeart = ouput.Read<CastleHeartConnection>().CastleHeartEntity._Entity;
                return inputHeart.Equals(outputHeart);
            }
            return false;
        }

        public static void TransferItems(ServerGameManager serverGameManager, Entity outputInventory, Entity inputInventory, PrefabGUID itemGuid, int transferAmount)
        {
            if (serverGameManager.TryRemoveInventoryItem(outputInventory, itemGuid, transferAmount))
            {
                if (serverGameManager.TryAddInventoryItem(inputInventory, itemGuid, transferAmount))
                {
                    Core.Log.LogInfo($"Moved {transferAmount} of {itemGuid.LookupName()} from Input to Output");
                }
                else
                {
                    Core.Log.LogInfo($"Failed to add {itemGuid.LookupName()}x{transferAmount} to OutputInventory, reverting...");
                    if (serverGameManager.TryAddInventoryItem(outputInventory, itemGuid, transferAmount))
                    {
                        Core.Log.LogInfo($"Restored items to original inventory.");
                    }
                    else
                    {
                        Core.Log.LogInfo($"Unable to return items to original inventory.");
                    }
                }
            }
            else
            {
                Core.Log.LogInfo($"Failed to remove {itemGuid.LookupName()}x{transferAmount} from Input");
            }
        }
    }
}
