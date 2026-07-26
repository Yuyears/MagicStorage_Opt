using MagicStorage.Common.Systems.RecurrentRecipes;
using MagicStorage.Common.Threading.Refreshing;
using System.Collections.Generic;
using Terraria;

namespace MagicStorage {
	partial class CraftingGUI {
	//	[ThreadStatic]
	//	internal static bool requestingAmountFromUI;

		internal static int AmountCraftable(Recipe recipe)
		{
			return AmountCraftable(NullThread, recipe);
		}

		internal static int AmountCraftable<T>(T thread, Recipe recipe)
			where T : RefreshThread, IProcessedStorageItemsProvider, IMainZoneFilterControlsProvider, IIngredientControlsProvider, ICraftingObjectProvider<Recipe>, ICraftObjectAvailableCacheProvider<Recipe>, IRecipeSimulationsProvider, IRecipeSnapshotsProvider
		{
			int maxCrafts;

			NetHelper.Report(true, "Calculating maximum amount to craft for current recipe...");

			if (recipe.createItem.maxStack == 1) {
				maxCrafts = IsAvailable(thread, recipe) ? 1 : 0;
				goto ReportAndReturn;
			}

			if (MagicStorageConfig.IsRecursionEnabled && recipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe)) {
				NetHelper.Report(false, "Recipe had a recursion tree");

				if (TryGetCachedAmountCraftable(thread, recipe, out maxCrafts))
					goto ReportAndReturn;

				if (thread?.RecipeSimulations.inventoryCraftabilityGraph.Value is { } graph
				&& graph.CanRejectMissingRecipes
				&& !graph.ProbeRecipe(recipe).HasCandidate) {
					maxCrafts = 0;
					StoreCachedAmountCraftable(thread, recipe, maxCrafts);
					goto ReportAndReturn;
				}

				if (!IsAvailable(thread, recipe)) {
					maxCrafts = 0;
					StoreCachedAmountCraftable(thread, recipe, maxCrafts);
					goto ReportAndReturn;
				}

				if (TryGetGraphBackedMaxCraftable(thread, recursiveRecipe, GetCurrentInventory(thread), out maxCrafts)) {
					StoreCachedAmountCraftable(thread, recipe, maxCrafts);
					goto ReportAndReturn;
				}

			//	using (FlagSwitch.ToggleTrue(ref requestingAmountFromUI))
				maxCrafts = recursiveRecipe.GetMaxCraftable(GetCurrentInventory(thread), thread?.cancellationToken ?? default);
				StoreCachedAmountCraftable(thread, recipe, maxCrafts);

				goto ReportAndReturn;
			}

			NetHelper.Report(false, "Recipe did not have a recursion tree or recursion was disabled");

			// Handle the old logic
			if (!IsAvailable(thread, recipe)) {
				maxCrafts = 0;
				goto ReportAndReturn;
			}

			Dictionary<int, int> storageQuantity = thread?.ProcessedStorageItems.itemCounts.Value ?? itemCounts;
			bool hasCreativeUnit = thread?.IngredientControls.creativeUnitPresent.Value ?? allItemsAreInfinite;
			HashSet<int> infiniteItems = thread?.IngredientControls.infiniteItems.Value ?? isItemInfinite;

			if (hasCreativeUnit) {
				// No ingredients would be consumed
				maxCrafts = Item.CommonMaxStack;
				goto ReportAndReturn;
			}

			int resultStack = recipe.createItem.stack;
			int low = 0;
			int high = (int)(Utility.CeilingMultiple(9999u, (uint)resultStack) / (uint)resultStack);
			while (low < high) {
				int middle = low + (high - low + 1) / 2;
				if (CanReserveRecipeBatches(recipe, storageQuantity, infiniteItems, middle))
					low = middle;
				else
					high = middle - 1;
			}

			maxCrafts = low * resultStack;

			ReportAndReturn:

			NetHelper.Report(false, $"Possible crafts = {maxCrafts}");

			return maxCrafts;
		}

		private static bool TryGetGraphBackedMaxCraftable<T>(T thread, RecursiveRecipe recursiveRecipe, AvailableRecipeObjects available, out int maxCraftable)
			where T : RefreshThread, IProcessedStorageItemsProvider, IMainZoneFilterControlsProvider, IIngredientControlsProvider, ICraftingObjectProvider<Recipe>, ICraftObjectAvailableCacheProvider<Recipe>, IRecipeSimulationsProvider, IRecipeSnapshotsProvider
		{
			maxCraftable = 0;

			int high = Item.CommonMaxStack;
			var highContext = CreateCraftingSimulationContext(thread, high);
			if (TryRunGraphBackedSimulation(thread, recursiveRecipe, high, available, highContext, out var highSimulation)) {
				maxCraftable = highSimulation.AmountCrafted;
				return true;
			}

			var oneContext = CreateCraftingSimulationContext(thread, 1);
			if (!TryRunGraphBackedSimulation(thread, recursiveRecipe, 1, available, oneContext, out var oneSimulation)
			|| oneSimulation.AmountCrafted <= 0)
				return false;

			int low = oneSimulation.AmountCrafted;
			while (low + 1 < high) {
				thread?.cancellationToken.ThrowIfCancellationRequested();

				int mid = low + (high - low) / 2;
				var midContext = CreateCraftingSimulationContext(thread, mid);
				if (TryRunGraphBackedSimulation(thread, recursiveRecipe, mid, available, midContext, out _))
					low = mid;
				else
					high = mid;
			}

			maxCraftable = low;
			return true;
		}
	}
}
