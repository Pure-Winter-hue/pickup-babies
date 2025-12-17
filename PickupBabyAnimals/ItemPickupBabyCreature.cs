using System;
using System.IO;
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

            // Cache intended spawn position first (ApplyStoredData may restore an old position from bytes)
            double spawnX = blockSel.Position.X + (blockSel.DidOffset ? 0 : blockSel.Face.Normali.X) + 0.5f;
            double spawnY = blockSel.Position.Y + (blockSel.DidOffset ? 0 : blockSel.Face.Normali.Y);
            double spawnZ = blockSel.Position.Z + (blockSel.DidOffset ? 0 : blockSel.Face.Normali.Z) + 0.5f;
            float spawnYaw = byEntity.Pos.Yaw + GameMath.PI;
            int spawnDim = blockSel.Position.dimension;

            entity.ServerPos.X = spawnX;
            entity.ServerPos.Y = spawnY;
            entity.ServerPos.Z = spawnZ;
            entity.ServerPos.Yaw = spawnYaw;
            entity.ServerPos.Dimension = spawnDim;
            entity.Pos.SetFrom(entity.ServerPos);
            entity.PositionBeforeFalling.Set(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z);

            // Re-apply all stored attributes (including tint, coat, genetics, etc.)
            ApplyStoredData(slot.Itemstack, entity);

            // Re-apply intended spawn position (in case ApplyStoredData restored an old position)
            entity.ServerPos.X = spawnX;
            entity.ServerPos.Y = spawnY;
            entity.ServerPos.Z = spawnZ;
            entity.ServerPos.Yaw = spawnYaw;
            entity.ServerPos.Dimension = spawnDim;
            entity.Pos.SetFrom(entity.ServerPos);
            entity.PositionBeforeFalling.Set(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z);

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
        /// Restores entity state that was saved when the creature was picked up.
        /// Prefers full Entity.ToBytes/FromBytes data if present, and falls back
        /// to Attributes/WatchedAttributes tree cloning for backwards compatibility.
        /// </summary>
        private void ApplyStoredData(ItemStack stack, Entity entity)
        {
            if (stack?.Attributes == null || entity == null) return;

            // Top-level attribute tree on the item
            TreeAttribute root = stack.Attributes as TreeAttribute;
            if (root == null) return;

            // ===== Full entity bytes (best preservation) =====
            // This will restore internal behavior state that isn't always represented
            // in Attributes/WatchedAttributes.
            byte[] bytes = null;
            try
            {
                bytes = root.GetBytes("pickupbabies.entityBytes", null);
            }
            catch
            {
                // ignore
            }

            if (bytes != null && bytes.Length > 0)
            {
                try
                {
                    using (var ms = new MemoryStream(bytes))
                    using (var br = new BinaryReader(ms))
                    {
                        // isSync=false because we're restoring a saved entity snapshot
                        entity.FromBytes(br, false);
                    }

                    // Never reuse a stored entity id
                    entity.EntityId = 0;
                }
                catch (Exception e)
                {
                    entity.World?.Logger?.Warning("ItemPickupBabyCreature: Failed to restore entity bytes ({0}). Falling back to attribute trees.", e.Message);
                }
            }

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