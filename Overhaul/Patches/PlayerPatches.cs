using System;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Patches
{
	[HarmonyPatch(typeof(Player))]
	public static class PlayerPatches
	{
		[HarmonyPatch(nameof(Player.SetControls))]
		[HarmonyPrefix]
		private static void SetControls_Prefix(Player __instance, ref Vector3 movedir,
			ref bool attack, ref bool attackHold, ref bool secondaryAttack,
			ref bool secondaryAttackHold, ref bool block, ref bool blockHold,
			ref bool jump, ref bool crouch, ref bool run, ref bool autoRun, ref bool dodge)
		{
			DynamicCombat.ProcessControls(__instance, ref movedir, ref attack, ref attackHold,
				ref secondaryAttack, ref secondaryAttackHold, ref block, ref blockHold,
				ref jump, ref crouch, ref run, ref autoRun, ref dodge);
		}

		[HarmonyPatch(nameof(Player.SetControls))]
		[HarmonyPostfix]
		private static void SetControls_Postfix(Player __instance)
		{
			DynamicCombat.Update(__instance);
		}

		[HarmonyPatch("QueueEquipAction")]
		[HarmonyPrefix]
        private static bool QueueEquipAction_Prefix(Player __instance, ItemDrop.ItemData item)
        {
            if (item == null || __instance.InAttack())
            {
                return true;
            }

            if (item.IsWeapon())
            {
                __instance.EquipItem(item, true);
                return false;
            }

            if (item.IsEquipable() && !item.IsWeapon())
            {
                __instance.EquipItem(item, true);
                return false;
            }

            return true;
        }

        [HarmonyPatch("QueueUnequipAction")]
        [HarmonyPrefix]
        private static bool QueueUnequipAction_Prefix(Player __instance, ItemDrop.ItemData item)
        {
            if (item == null || __instance.InAttack())
            {
                return true;
            }

            if (item.IsWeapon())
            {
                __instance.UnequipItem(item, true);
                return false;
            }

            if (item.IsEquipable() && !item.IsWeapon())
            {
                __instance.UnequipItem(item, true);
                return false;
            }

            return true;
        }

		[HarmonyPatch(typeof(Character), "UpdateWalking")]
		private class Character_UpdateWalking_Patch
		{
			public static bool Prefix(Character __instance, float dt)
			{
				if (__instance.IsPlayer())
				{
					__instance.m_speed = OverhaulConfig.BaseMovementSpeed.Value;
					__instance.m_crouchSpeed = OverhaulConfig.SneakMovementSpeed.Value;
					Player player = __instance as Player;
					if (DynamicCombat.UpdateAttackLunge(player, dt))
						return false;
					if (DynamicCombat.IsStaffShieldStunned(player)
						|| DynamicCombat.IsStaffShieldCasting(player))
					{
						Vector3 velocity = player.m_body.linearVelocity;
						player.m_body.linearVelocity = new Vector3(0f, velocity.y, 0f);
						player.m_currentVel = player.m_body.linearVelocity;
						return false;
					}
					if (DynamicCombat.IsDashing(player))
					{
						DynamicCombat.UpdateDash(player, dt);
						return false;
					}
				}
				return true;
			}
		}

        [HarmonyPatch(typeof(Character), "UpdateSwimming")]
        private class Character_UpdateSwimming_Patch
        {
            public static bool Prefix(Character __instance, float dt)
            {
                Player player = __instance as Player;
                if (!DynamicCombat.IsDashing(player)) return true;
                // Walking and swimming are mutually exclusive native movement paths.
                // Advance the same dash here so water cannot pause it until landfall.
                DynamicCombat.UpdateDash(player, dt);
                return false;
            }
        }

		[HarmonyPatch(typeof(Player), "CheckRun")]
		private class Player_CheckRun_Patch
		{
			public static bool Prefix(ref bool __result)
			{
				__result = false;
				return false;
			}
		}

		[HarmonyPatch(typeof(Player), "OnJump")]
		private class Player_OnJump_Patch
		{
			public static void Postfix(Player __instance)
			{
				DynamicCombat.OnJump(__instance);
			}

			public static void Prefix(Player __instance)
			{
				if (__instance.IsPlayer())
				{
					if (OverhaulConfig.JumpUseStamina.Value)
					{
						__instance.m_jumpStaminaUsage = OverhaulConfig.JumpStaminaDrain.Value;
						return;
					}
					__instance.m_jumpStaminaUsage = 0f;
				}
			}
		}

		[HarmonyPatch(typeof(Player), "UpdateStats")]
		[HarmonyPatch(new Type[]
		{
			typeof(float)
		})]
		private class Player_UpdateStats_Patch
		{
			public static void Prefix(Player __instance)
			{
				if (__instance.IsPlayer())
				{
					if (OverhaulConfig.EncumberedUseStamina.Value)
					{
						__instance.m_encumberedStaminaDrain = OverhaulConfig.EncumberedStaminaDrain.Value;
						return;
					}
					__instance.m_encumberedStaminaDrain = 0f;
				}
			}
		}

		[HarmonyPatch(typeof(ItemDrop.ItemData), "GetDrawStaminaDrain")]
		private class ItemDrop_GetDrawStaminaDrain_Patch
		{
			public static void Postfix(ref float __result)
			{
				if (OverhaulConfig.BowUseStamina.Value)
				{
					__result *= OverhaulConfig.BowStaminaDrainRate.Value;
					return;
				}
				__result = 0f;
			}
		}

		[HarmonyPatch(typeof(Player), "OnSwimming")]
		private class Player_OnSwimming_Patch
		{
			public static void Prefix(Player __instance)
			{
				if (__instance.IsPlayer())
				{
					float drain = OverhaulConfig.SwimUseStamina.Value ? OverhaulConfig.SwimStaminaDrain.Value : 0f;
					__instance.m_swimStaminaDrainMinSkill = drain;
					__instance.m_swimStaminaDrainMaxSkill = drain;
				}
			}
		}

		[HarmonyPatch(typeof(Player), "OnSneaking")]
		private class Player_OnSneaking_Patch
		{
			public static void Prefix(Player __instance)
			{
				if (__instance.IsPlayer())
				{
					if (OverhaulConfig.SneakUseStamina.Value)
					{
						__instance.m_sneakStaminaDrain = OverhaulConfig.SneakStaminaDrain.Value;
						return;
					}
					__instance.m_sneakStaminaDrain = 0f;
				}
			}
		}

		[HarmonyPatch(typeof(Player), "GetBuildStamina")]
		private class Player_GetBuildStamina_Patch
		{
			public static void Postfix(Player __instance, ref float __result)
			{
				if (__instance.RightItem == null) return;

				string name = __instance.RightItem.m_shared.m_name;
				if ((name == "$item_hammer" && !OverhaulConfig.BuildUseStamina.Value)
					|| (name == "$item_hoe" && !OverhaulConfig.TerrainUseStamina.Value)
					|| (name == "$item_cultivator" && !OverhaulConfig.FarmUseStamina.Value))
				{
					__result = 0f;
				}
			}
		}

		[HarmonyPatch(typeof(Player), "UpdateStats", new Type[] { typeof(float) })]
		private class Player_StaminaRegen_UpdateStats_Patch
		{
			[HarmonyPostfix]
			public static void Postfix(Player __instance, float dt)
			{
				float vanillaMultiplier = GetVanillaStaminaRegenMultiplier(__instance);
				float overhaulMultiplier = GetOverhaulStaminaRegenMultiplier(__instance);
				float missingMultiplier = overhaulMultiplier - vanillaMultiplier;
				if (missingMultiplier <= 0f || __instance.m_staminaRegenTimer > 0f)
				{
					return;
				}

				float maxStamina = __instance.GetMaxStamina();
				if (__instance.m_stamina >= maxStamina || maxStamina <= 0f)
				{
					return;
				}

				float regen = __instance.m_staminaRegen
					+ (1f - __instance.m_stamina / maxStamina)
					* __instance.m_staminaRegen
					* __instance.m_staminaRegenTimeMultiplier;
				float statusMultiplier = 1f;
				__instance.m_seman.ModifyStaminaRegen(ref statusMultiplier);
				regen *= statusMultiplier;

				__instance.m_stamina = Mathf.Min(
					maxStamina,
					__instance.m_stamina + regen * missingMultiplier * dt * Game.m_staminaRegenRate);
				__instance.m_nview.GetZDO().Set(ZDOVars.s_stamina, __instance.m_stamina);
			}

			private static float GetVanillaStaminaRegenMultiplier(Player player)
			{
				float multiplier = player.IsBlocking() ? 0.8f : 1f;
				if ((player.IsSwimming() && !player.IsOnGround()) || player.InAttack()
					|| player.InDodge() || player.m_wallRunning || player.IsEncumbered())
				{
					return 0f;
				}
				return multiplier;
			}

			private static float GetOverhaulStaminaRegenMultiplier(Player player)
			{
				bool blockUsesStamina = OverhaulConfig.BlockUseStamina.Value
					&& OverhaulConfig.BlockStaminaDrain.Value > 0f;
				float multiplier = player.IsBlocking() && blockUsesStamina ? 0.8f : 1f;

				if (player.InDodge() || player.m_wallRunning)
				{
					return 0f;
				}
				// Swimming always prevents regeneration, even when its stamina cost is disabled.
				if (player.IsSwimming() && !player.IsOnGround())
				{
					return 0f;
				}
				if (player.InAttack())
				{
					bool attackUsesStamina = player.IsDrawingBow()
						? OverhaulConfig.BowUseStamina.Value && OverhaulConfig.BowStaminaDrainRate.Value > 0f
						: OverhaulConfig.AttackUseStamina.Value && OverhaulConfig.AttackStaminaDrain.Value > 0f;
					if (attackUsesStamina) return 0f;
				}
				if (player.IsEncumbered() && OverhaulConfig.EncumberedUseStamina.Value
					&& OverhaulConfig.EncumberedStaminaDrain.Value > 0f)
				{
					return 0f;
				}

				return multiplier;
			}
		}

		[HarmonyPatch(typeof(FishingFloat), "FixedUpdate")]
		private class FishingFloat_FixedUpdate_Patch
		{
			[HarmonyPrepare]
			public static bool Prepare()
			{
				bool available = AccessTools.DeclaredMethod(typeof(FishingFloat), "FixedUpdate") != null;
				if (!available) Log.LogWarning("FishingFloat.FixedUpdate introuvable : gestion de stamina de pêche désactivée.");
				return available;
			}

			public static void Prefix(FishingFloat __instance, out float[] __state)
			{
				__state = new[] { __instance.m_hookedStaminaPerSec, __instance.m_hookedStaminaPerSecMaxSkill, __instance.m_pullStaminaUse };
				if (!OverhaulConfig.FishUseStamina.Value)
				{
					__instance.m_hookedStaminaPerSec = 0f;
					__instance.m_hookedStaminaPerSecMaxSkill = 0f;
					__instance.m_pullStaminaUse = 0f;
				}
			}

			public static void Postfix(FishingFloat __instance, float[] __state)
			{
				__instance.m_hookedStaminaPerSec = __state[0];
				__instance.m_hookedStaminaPerSecMaxSkill = __state[1];
				__instance.m_pullStaminaUse = __state[2];
			}
		}

        [HarmonyPatch("Interact")]
		[HarmonyPrefix]
        public static void Interact_Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (__instance.InAttack() || __instance.InDodge())
            {
                return;
            }
            if (hold && Time.time - __instance.m_lastHoverInteractTime < 0.2f)
            {
                return;
            }
            Interactable componentInParent = go.GetComponentInParent<Interactable>();
            Pickable pickable = componentInParent as Pickable;
            if (pickable != null)
            {
                if (global::Overhaul.Persistence.PlayerSessionGame.Managed) return;
                foreach (Collider collider in Physics.OverlapSphere(go.transform.position, OverhaulConfig.PickupRange.Value, __instance.m_interactMask))
                {
                    Pickable pickable2;
                    if (collider == null)
                    {
                        pickable2 = null;
                    }
                    else
                    {
                        GameObject gameObject = collider.gameObject;
                        pickable2 = ((gameObject != null) ? gameObject.GetComponentInParent<Pickable>() : null);
                    }
                    Pickable pickable3 = pickable2;
                    if (pickable3 != null && pickable3 != pickable && pickable3.m_itemPrefab.name == pickable.m_itemPrefab.name)
                    {
                        pickable3.Interact(__instance, false, alt);
                    }
                }
                return;
            }
            Beehive beehive = componentInParent as Beehive;
            if (beehive != null)
            {
                if (global::Overhaul.Persistence.PlayerSessionGame.Managed) return;
                foreach (Collider collider2 in Physics.OverlapSphere(go.transform.position, OverhaulConfig.PickupRange.Value, __instance.m_interactMask))
                {
                    Beehive beehive2;
                    if (collider2 == null)
                    {
                        beehive2 = null;
                    }
                    else
                    {
                        GameObject gameObject2 = collider2.gameObject;
                        beehive2 = ((gameObject2 != null) ? gameObject2.GetComponentInParent<Beehive>() : null);
                    }
                    Beehive beehive3 = beehive2;
                    if (beehive3 != null && beehive3 != beehive && PrivateArea.CheckAccess(beehive3.transform.position, 0f, true, false))
                    {
						beehive3.Extract();
                    }
                }
            }
        }
    }
}



