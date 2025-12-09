using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace PickupBabyAnimals
{
    /// <summary>
    /// Creature item that remembers full stored attributes on the stack and reapplies them
    /// when the creature is placed again (colour, genetics, generation, etc.).
    /// </summary>
    public class ItemPickupBabyCreature : Item
    {
        // Same interaction entry point as normal item placement
        public override void OnHeldInteractStart(
            ItemSlot slot,
            EntityAgent byEntity,
            BlockSelection blockSel,
            EntitySelection entitySel,
            bool firstEvent,
            ref EnumHandHandling handHandling)
        {
            if (blockSel == null) return;

            if (!(byEntity is EntityPlayer eplr)) return;

            IPlayer player = byEntity.World.PlayerByUid(eplr.PlayerUID);
            if (!byEntity.World.Claims.TryAccess(player, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
            {
                return;
            }

            // Figure out which entity type to spawn
            AssetLocation location;

            // Preferred: explicit code saved when picking the baby up
            string storedEntityCode = slot.Itemstack?.Attributes?.GetString("pickupbabies.entityCode", null);
            if (!string.IsNullOrEmpty(storedEntityCode))
            {
                location = new AssetLocation(storedEntityCode);
            }
            else
            {
                // Fallback: behave like vanilla ItemCreature – derive from own code
                location = new AssetLocation(Code.Domain, CodeEndWithoutParts(1));
            }

            EntityProperties type = byEntity.World.GetEntityType(location);
            if (type == null)
            {
                byEntity.World.Logger.Error("ItemPickupBabyCreature: No such entity - {0}", location);
                return;
            }

            // Create the entity and position it like ItemCreature does
            Entity entity = byEntity.World.ClassRegistry.CreateEntity(type);
            if (entity == null) return;

            entity.ServerPos.X = blockSel.Position.X + (blockSel.DidOffset ? 0 : blockSel.Face.Normali.X) + 0.5f;
            entity.ServerPos.Y = blockSel.Position.Y + (blockSel.DidOffset ? 0 : blockSel.Face.Normali.Y);
            entity.ServerPos.Z = blockSel.Position.Z + (blockSel.DidOffset ? 0 : blockSel.Face.Normali.Z) + 0.5f;
            entity.ServerPos.Yaw = byEntity.Pos.Yaw + GameMath.PI;
            entity.ServerPos.Dimension = blockSel.Position.dimension;
            entity.Pos.SetFrom(entity.ServerPos);
            entity.PositionBeforeFalling.Set(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z);

            // Re-apply all stored attributes (including tint, coat, genetics, etc.)
            ApplyStoredData(slot.Itemstack, entity);

            // Ensure origin is set to playerplaced (overrides whatever we restored)
            entity.Attributes.SetString("origin", "playerplaced");

            // Spawn the entity
            byEntity.World.SpawnEntity(entity);

            // ONLY consume item after successful spawn (moved from top)
            if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                slot.TakeOut(1);
                slot.MarkDirty();
            }

            handHandling = EnumHandHandling.PreventDefaultAction;
        }

        /// <summary>
        /// Restores the full Attributes and WatchedAttributes trees that were saved
        /// when the baby was picked up.
        /// </summary>
        /// <summary>
        /// Restores the full Attributes and WatchedAttributes trees that were saved
        /// when the baby was picked up.
        /// </summary>
        private void ApplyStoredData(ItemStack stack, Entity entity)
        {
            if (stack?.Attributes == null || entity == null) return;

            // Top-level attribute tree on the item
            ITreeAttribute root = stack.Attributes;
            if (root == null) return;

            // ===== Normal Attributes =====
            // These were saved as a cloned TreeAttribute on the stack.
            TreeAttribute storedAttr = root.GetTreeAttribute("pickupbabies.attributes") as TreeAttribute;
            ITreeAttribute destAttrTree = entity.Attributes as ITreeAttribute;

            if (storedAttr != null && destAttrTree != null)
            {
                // IMPORTANT: do not assume SyncedTreeAttribute here – just use the interface.
                // Clear what the freshly spawned entity had.
                if (destAttrTree is TreeAttribute destTreeConcrete)
                {
                    destTreeConcrete.Clear();

                    foreach (var kv in storedAttr)
                    {
                        destTreeConcrete[kv.Key] = kv.Value.Clone();
                    }
                }
                else
                {
                    // Fallback for non-TreeAttribute implementations
                    foreach (var kv in storedAttr)
                    {
                        destAttrTree[kv.Key] = kv.Value.Clone();
                    }
                }
            }

            // ===== Watched Attributes =====
            // These were also saved as a cloned TreeAttribute.
            TreeAttribute storedWatched = root.GetTreeAttribute("pickupbabies.watchedAttributes") as TreeAttribute;
            ITreeAttribute destWatchedTree = entity.WatchedAttributes as ITreeAttribute;

            if (storedWatched != null && destWatchedTree != null)
            {
                // Again, *never* cast to SyncedTreeAttribute. Treat it generically...
                if (destWatchedTree is TreeAttribute destWatchedConcrete)
                {
                    destWatchedConcrete.Clear();

                    foreach (var kv in storedWatched)
                    {
                        destWatchedConcrete[kv.Key] = kv.Value.Clone();
                    }
                }
                else
                {
                    // Fallback: interface-only path
                    foreach (var kv in storedWatched)
                    {
                        destWatchedTree[kv.Key] = kv.Value.Clone();
                    }
                }
            }
        }


    }
}