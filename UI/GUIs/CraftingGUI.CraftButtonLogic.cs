using MagicStorage.Common.Systems;
using Microsoft.Xna.Framework.Input;
using System;
using Terraria;
using Terraria.Audio;
using Terraria.ID;

namespace MagicStorage {
	partial class CraftingGUI {
		public static int craftAmountTarget = 1;

		internal static int craftTimer;
		internal static int maxCraftTimer = StartMaxCraftTimer;

		internal static void ClickCraftButton(ref bool stillCrafting) {
			if (craftTimer <= 0) {
				craftTimer = maxCraftTimer;
				maxCraftTimer = maxCraftTimer * 3 / 4;
				if (maxCraftTimer <= 0)
					maxCraftTimer = 1;

				int amount = craftAmountTarget;

				if (MagicStorageConfig.UseOldCraftMenu && Main.keyState.IsKeyDown(Keys.LeftControl))
					amount = Item.CommonMaxStack;

				bool crafted = Craft(amount);

				if (crafted && Main.netMode != NetmodeID.MultiplayerClient)
					SoundEngine.PlaySound(SoundID.Grab);
			}

			craftTimer--;
			stillCrafting = true;
		}

		internal static void ClickAmountButton(int amount, bool offset) {
			if (MagicUI.CurrentlyRefreshing)
				return;  // Do not read anything until refreshing is completed

			int oldTarget = craftAmountTarget;
			if (offset && (amount == 1 || craftAmountTarget > 1))
				craftAmountTarget += amount;
			else
				craftAmountTarget = amount;  //Snap directly to the amount if the amount target was 1 (this makes clicking 10 when at 1 just go to 10 instead of 11)

			ClampCraftAmount(oldTarget);

			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		internal static void ClampCraftAmount(int? fallbackTarget = null) {
			if (MagicUI.CurrentlyRefreshing)
				return;  // Recipe/ingredient information may not be available

			// Ensure that multiple accesses use the same object
			var recipe = selectedRecipe;

			int oldTarget = craftAmountTarget;
			int newTarget = craftAmountTarget;

			if (newTarget >= 1 && recipe is not null && recipe.createItem.maxStack != 1 && IsCurrentRecipeAvailable()) {
				if (Main.netMode == NetmodeID.MultiplayerClient
				&& MagicStorageConfig.IsRecursionEnabled
					&& recipe.HasRecursiveRecipe()) {
					if (newTarget >= Item.CommonMaxStack) {
						int requestedMax = recipe.createItem.maxStack;
						craftAmountTarget = Utils.Clamp(fallbackTarget ?? 1, 1, requestedMax);
						amountCraftableForCurrentRecipe = null;
						StartSelectedRecipeRefreshThread(recipe, requestedMax, caller: "CraftingGUI.ClampCraftAmount()");
						return;
					}

					if (object.ReferenceEquals(recentRecipeAmountCraftable, recipe) && amountCraftableForCurrentRecipe is { } cachedAmount)
						newTarget = Utils.Clamp(newTarget, 1, Math.Min(cachedAmount, recipe.createItem.maxStack));
					else {
						craftAmountTarget = Math.Min(newTarget, recipe.createItem.maxStack);
						StartSelectedRecipeRefreshThread(recipe, craftAmountTarget, caller: "CraftingGUI.ClampCraftAmount()");
						return;
					}

					goto ApplyTarget;
				}

				if (newTarget > (amountCraftableForCurrentRecipe ?? 0))
					amountCraftableForCurrentRecipe = null;

				int amountCraftable = AmountCraftableForCurrentRecipe();
				int max = Utils.Clamp(amountCraftable, 1, recipe.createItem.maxStack);

				if (newTarget > max)
					newTarget = max;
			} else
				newTarget = 1;

			ApplyTarget:
			if (oldTarget != newTarget) {
				craftAmountTarget = newTarget;

				// If "selectedRecipe" has changed, then the UI would be refreshed anyway; don't start a new thread
				if (MagicStorageConfig.IsRecursionEnabled && recipe is not null && object.ReferenceEquals(recipe, selectedRecipe))
					StartSelectedRecipeRefreshThread(recipe, newTarget, caller: "CraftingGUI.ClampCraftAmount()");
			}
		}
	}
}
