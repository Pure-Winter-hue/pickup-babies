using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace PickupBabyAnimals   
{
    public class PickupBabyAnimalsModSystem : ModSystem
    {
        private ICoreServerAPI sapi;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Register our custom item class used by capturedbaby.json
            api.RegisterItemClass("ItemPickupBabyCreature", typeof(ItemPickupBabyCreature));

            if (api.Side == EnumAppSide.Server)
            {
                sapi = (ICoreServerAPI)api;
                sapi.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
            }
        }

        private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            try
            {
                HandlePlayerInteractEntity(entity, byPlayer, slot, hitPosition, mode, ref handling);
            }
            catch (Exception e)
            {
                sapi?.Logger.Error("PickupBabyAnimals: exception in interaction handler: {0}", e);
            }
        }

        private void HandlePlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            if (handling == EnumHandling.PreventDefault) return;
            if (entity == null || byPlayer == null) return;

            // Only “use / interact” right-click
            if ((EnumInteractMode)mode != EnumInteractMode.Interact) return;

            var ePlayer = byPlayer.Entity as EntityPlayer;
            if (ePlayer == null) return;

            var controls = ePlayer.Controls;
            if (controls == null) return;

            // === Sprint + right click on wolf pups => creature item (PickUpPuppies-style) ===
            if (controls.Sprint)
            {
                // Only wolf pups are handled here; others just fall through to vanilla / other logic
                if (TryPickupWolfPupAsItem(entity, ePlayer))
                {
                    // We handled it, block vanilla and other handlers
                    handling = EnumHandling.PreventDefault;
                }

                // Important: never fall through into the "bag" logic when sprinting
                return;
            }

            // === Sneak + right click => existing capturedbaby bag behaviour ===
            // Require sneaking
            if (!controls.Sneak) return;

            // Require empty hand (avoid overwriting held item)
            if (slot == null || !slot.Empty) return;

            // Only baby / juvenile entities
            if (!LooksLikeJuvenile(entity)) return;

            // Build the stack for our capturedbaby item and copy entity data into it
            ItemStack stack = CreateCapturedBabyStack(entity);
            if (stack == null) return;

            // Put into the interacted slot (active hotbar)
            slot.Itemstack = stack;
            slot.MarkDirty();

            // Stop vanilla handling (e.g. milking, shearing)
            handling = EnumHandling.PreventDefault;

            // Despawn without killing – NO entity.Die() - yeah it happened, lol
            var sworld = entity.World as IServerWorldAccessor;
            if (sworld != null)
            {
                var despawnData = new EntityDespawnData();
                despawnData.Reason = EnumDespawnReason.PickedUp;
                sworld.DespawnEntity(entity, despawnData);
            }
        }

        /// <summary>
        /// Sprint + right click: pick up wolf pups as simple creature items,
        /// without storing full attributes on a capturedbaby stack.
        /// </summary>
        private bool TryPickupWolfPupAsItem(Entity entity, EntityPlayer ePlayer)
        {
            if (sapi == null) return false;
            if (entity == null || ePlayer == null) return false;
            if (entity.World.Side != EnumAppSide.Server) return false;

            // Work out which wolf pup item this entity should turn into
            if (!TryGetWolfPupItemCode(entity, out AssetLocation pupItemCode)) return false;

            Item pupItem = sapi.World.GetItem(pupItemCode);
            if (pupItem == null)
            {
                sapi.Logger.Error("PickupBabyAnimals: Could not resolve wolf pup item {0}", pupItemCode);
                return false;
            }

            ItemStack pupStack = new ItemStack(pupItem);

            var sPlayer = ePlayer.Player as IServerPlayer;
            bool fullyGiven = ePlayer.TryGiveItemStack(pupStack);

            if (!fullyGiven)
            {
                sapi.SendIngameError(
                    sPlayer,
                    "pickupbabyanimals-puppy-invfull",
                    "No space in your inventory for a wolf pup."
                );
                return false;
            }

            // Match PickUpPuppies behaviour: despawn via Die(PickedUp)
            entity.Die(EnumDespawnReason.PickedUp);

            return true;
        }

        /// <summary>
        /// For a wolf pup entity, determines the correct creature item code to give.
        /// Mirrors the logic from PickUpPuppies.
        /// </summary>
        private bool TryGetWolfPupItemCode(Entity entity, out AssetLocation itemCode)
        {
            itemCode = null;

            AssetLocation ecode = entity.Code;
            if (ecode == null) return false;

            string domain = ecode.Domain ?? "game";
            string path = ecode.Path ?? "";

            // Normal wild pup entities
            if (path.StartsWith("wolf-eurasian-baby-"))
            {
                // Turn into corresponding creature item
                itemCode = new AssetLocation(domain, "creature-" + path);
                return true;
            }

            // Fallback: in case some variant already has "creature-" in the path
            if (path.StartsWith("creature-wolf-eurasian-baby-"))
            {
                itemCode = new AssetLocation(domain, path);
                return true;
            }

            return false;
        }


        /// <summary>
        /// Creates the capturedbaby item stack and stores all relevant entity data on it.
        /// </summary>
        private ItemStack CreateCapturedBabyStack(Entity entity)
        {
            if (sapi == null) return null;

            // Single universal item, see capturedbaby.json
            Item capturedItem = sapi.World.GetItem(new AssetLocation("pickupbabyanimals", "capturedbaby"));
            if (capturedItem == null)
            {
                sapi.Logger.Error("PickupBabyAnimals: could not resolve item pickupbabyanimals:capturedbaby");
                return null;
            }

            var stack = new ItemStack(capturedItem);

            // Ensure we have a TreeAttribute root
            TreeAttribute root = stack.Attributes as TreeAttribute ?? new TreeAttribute();
            stack.Attributes = root;

            // Entity code to respawn later
            if (entity.Code != null)
            {
                root.SetString("pickupbabies.entityCode", entity.Code.ToShortString());
            }

            // Full attributes tree
            if (entity.Attributes is ITreeAttribute attrTree)
            {
                root["pickupbabies.attributes"] = attrTree.Clone();
            }

            // Full watched attributes tree (generation, genetics, colour, etc.)
            if (entity.WatchedAttributes is ITreeAttribute watchedTree)
            {
                root["pickupbabies.watchedAttributes"] = watchedTree.Clone();
            }

            return stack;
        }

        // ===== Juvenile detection (heuristic, adapted from PW's entity color tint system) =====
        private static bool LooksLikeJuvenile(Entity e)
        {
            if (e == null) return false;

            // juvenile-ish words to look for.
            string[] juvenileWords =
            {
                "baby","juvenile","child","young","youth","adolescent",
                "calf","kid","fawn","lamb","foal","pup","puppy",
                "kitten","kit","cub","gosling","duckling","chick",
                "piglet","joey","offspring","cygnet","cria","eyas",
                "leveret","puggle","puggles","squab","owlet",
                "spiderling","hatchling","poult","pullet","whelp"
            };

            // 1. Read typical age / stage variants
            string age = GetVariant(e, "age").ToLowerInvariant();
            string stage = GetVariant(e, "stage").ToLowerInvariant();
            string ls1 = GetVariant(e, "lifestage").ToLowerInvariant();
            string ls2 = GetVariant(e, "lifeStage").ToLowerInvariant();

            //  If anything explicitly says "adult", *force* adult
            if (age == "adult" || stage == "adult" || ls1 == "adult" || ls2 == "adult")
                return false;

            // Helper: does this variant string look juvenile?
            bool VariantLooksJuvenile(string v)
            {
                if (string.IsNullOrEmpty(v)) return false;
                foreach (var w in juvenileWords)
                {
                    if (v.Contains(w)) return true;
                }
                return false;
            }

            //  If any variant clearly looks juvenile, treat as juvenile
            if (VariantLooksJuvenile(age) || VariantLooksJuvenile(stage) ||
                VariantLooksJuvenile(ls1) || VariantLooksJuvenile(ls2))
                return true;

            //  Fall back to entity code path hints (works across many mods)
            string path = e.Code?.Path?.ToLowerInvariant() ?? "";

            foreach (var w in juvenileWords)
            {
                if (path.Contains(w)) return true;
            }

            // Default: assume adult if nothing clearly says "baby"
            return false;
        }

        private static string GetVariant(Entity e, string name)
        {
            if (e == null) return "";

            try
            {
                // First: proper variant definitions on the entity type
                var dict = e.Properties?.Variant;
                if (dict != null && dict.TryGetValue(name, out string val) && !string.IsNullOrEmpty(val))
                {
                    return val;
                }
            }
            catch
            {
                // ignore variant lookup errors
            }

            // Then: watched attributes (synced)
            string v = e.WatchedAttributes?.GetString(name, "") ?? "";
            if (!string.IsNullOrEmpty(v)) return v;

            // Finally: normal attributes
            v = e.Attributes?.GetString(name, "") ?? "";
            return v ?? "";
        }
    }
}

