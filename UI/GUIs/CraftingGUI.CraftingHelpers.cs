using System.Collections.Generic;
using System;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria;
using System.Linq;
using MagicStorage.Components;
using System.Runtime.CompilerServices;
using MagicStorage.CrossMod;
using MagicStorage.Common.Systems.RecurrentRecipes;
using Terraria.DataStructures;

namespace MagicStorage {
	partial class CraftingGUI {
		internal static readonly List<ItemData> blockStorageItems = new();

		[ThreadStatic]
		internal static bool _simulatingCrafts;
		/// <summary>
		/// Whether crafting simulations are currently being performed.  Use of this property is encouraged if a recipe does more than spawn items on the player.
		/// </summary>
		public static bool SimulatingCrafts {
			[MethodImpl(MethodImplOptions.NoInlining)]
			get => _simulatingCrafts;
		}

		internal static CraftingContext InitCraftingContext(Recipe recipe, int toCraft) {
			var sourceItems = storageItems.Where(item => !blockStorageItems.Contains(item)).ToList();
			var availableItems = sourceItems.Select(item => item.Clone()).ToList();
			var fromModule = sourceItemsFromModules.Where(item => !blockStorageItems.Contains(item)).ToList();
			List<Item> toWithdraw = new(), results = new();

			TEStorageHeart heart = GetHeart();
			Player player = Main.LocalPlayer;

			EnvironmentSandbox sandbox = new(player, heart);

			return new CraftingContext() {
				sourceItems = sourceItems,
				availableItems = availableItems,
				toWithdraw = toWithdraw,
				results = results,
				itemCounts = GetItemCountsWithBlockedItemsRemoved(),
				sandbox = sandbox,
				consumedItemsFromModules = new(),
				sourceItemsFromModules = fromModule,
				modules = heart?.GetModules().ToArray() ?? Array.Empty<EnvironmentModule>(),
				toCraft = toCraft,
				recipe = recipe,
				player = player,
				heart = heart,
				environment = ReadCraftingEnvironment()
			};
		}

		private static CraftingContext InitServerCraftingContext(Player player, TEStorageHeart heart, TECraftingAccess access, Recipe recipe, int toCraft, bool captureRecipeConditions = false) {
			int previousPlayer = Main.myPlayer;
			Main.myPlayer = player.whoAmI;
			try {
				EnvironmentModule[] modules = heart.GetModules().ToArray();
				EnvironmentSandbox sandbox = new(player, heart);
				List<Item> sourceItems = heart.GetStoredItems().Where(static item => !item.IsAir).Select(static item => item.Clone()).ToList();
				List<Item> moduleItems = [];
				HashSet<Item> seenModuleItems = new(ReferenceEqualityComparer.Instance);
				foreach (EnvironmentModule module in modules) {
					foreach (Item item in module.GetAdditionalItems(sandbox) ?? []) {
						if (item is { IsAir: false } && seenModuleItems.Add(item))
							moduleItems.Add(item);
					}
				}
				List<Item> moduleSnapshot = moduleItems.Select(static item => item.Clone()).ToList();

				Dictionary<int, int> counts = new();
				foreach (Item item in sourceItems.Concat(moduleSnapshot))
					counts.AddOrSumCount(item.type, item.stack);

				CraftingInformation environment = new(false, false, false, false, false, false, false, false, new bool[TileLoader.TileCount]);
				if (access is not null) {
					foreach (Item station in access.stations)
						Utility.AddCraftingZones(player, station, ref environment);
					environment.adjTiles[ModContent.TileType<CraftingAccess>()] = true;
				}
				foreach (EnvironmentModule module in modules)
					module.ModifyCraftingZones(sandbox, ref environment);

				HashSet<int> infiniteItems = sandbox.LoadInfiniteItems();
				bool creativeUnitPresent = sandbox.HeartHasCreativeUnit();
				bool[] recipeConditions = null;
				if (captureRecipeConditions) {
					recipeConditions = new bool[Recipe.numRecipes];
					ExecuteInCraftingEnvironment(player, environment, () => {
						for (int i = 0; i < Recipe.numRecipes; i++)
							recipeConditions[i] = Utility.IsAvailableForSnapshot(Main.recipe[i]);
					});
				}

				return new CraftingContext {
					sourceItems = sourceItems,
					availableItems = sourceItems.Select(static item => item.Clone()).ToList(),
					toWithdraw = [],
					results = [],
					itemCounts = counts,
					sourceItemsFromModules = moduleSnapshot,
					sandbox = sandbox,
					consumedItemsFromModules = [],
					moduleItemsToCommit = moduleItems,
					modules = modules,
					toCraft = toCraft,
					recipe = recipe,
					player = player,
					heart = heart,
					environment = environment,
					availableRecipeObjects = new AvailableRecipeObjects(environment.adjTiles, counts, recipeConditions, infiniteItems, creativeUnitPresent,
						captureRecipeConditions ? null : static candidate => !candidate.Disabled && RecipeLoader.RecipeAvailable(candidate))
				};
			} finally {
				Main.myPlayer = previousPlayer;
			}
		}

		internal static bool TryPlanServerItemConsumption(Player player, TEStorageHeart heart, IEnumerable<Item> requirements, out List<Item> storageItems, out List<Item> moduleItems, out List<Item> moduleItemsToCommit) {
			CraftingContext context = InitServerCraftingContext(player, heart, null, null, 0);

			foreach (Item requirement in requirements) {
				int stack = requirement.stack;
				if (stack <= 0 || !AttemptToConsumeItem(context, requirement.type, ref stack, checkRecipeGroup: false) || stack > 0) {
					storageItems = [];
					moduleItems = [];
					moduleItemsToCommit = [];
					return false;
				}
			}

			storageItems = CompactItemList(context.toWithdraw);
			moduleItems = CompactItemList(context.consumedItemsFromModules);
			moduleItemsToCommit = context.moduleItemsToCommit;
			return true;
		}

		private static bool CanConsumeItem(CraftingContext context, Item reqItem, List<Item> origWithdraw, List<Item> origResults, List<Item> origFromModule, out bool wasAvailable, out int stackConsumed, bool checkRecipeGroup = true) {
			wasAvailable = true;

			stackConsumed = reqItem.stack;

			RecipeLoader.ConsumeIngredient(context.recipe, reqItem.type, ref stackConsumed, isDecrafting: false);

			foreach (EnvironmentModule module in context.modules)
				module.ConsumeItemForRecipe(context.sandbox, context.recipe, reqItem.type, ref stackConsumed);

			// FIX: v0.7.0.9 - Ingredient reductions from callbacks like the one from using the Alchemy Table weren't respected in the consumption process
			reqItem.stack = stackConsumed;

			if (stackConsumed <= 0)
				return false;

			int stack = stackConsumed;
			bool consumeSucceeded = AttemptToConsumeItem(context, reqItem.type, ref stack, checkRecipeGroup);

			if (stack > 0 || !consumeSucceeded) {
				context.results.Clear();
				context.results.AddRange(origResults);

				context.toWithdraw.Clear();
				context.toWithdraw.AddRange(origWithdraw);

				context.consumedItemsFromModules.Clear();
				context.consumedItemsFromModules.AddRange(origFromModule);

				wasAvailable = false;
				return false;
			}

			return true;
		}

		internal static bool AttemptToConsumeItem(CraftingContext context, int reqType, ref int stack, bool checkRecipeGroup = true) {
			return CheckContextItemCollection(context, context.results, reqType, ref stack, null, checkRecipeGroup)
				|| CheckContextItemCollection(context, GetAvailableItems(context), reqType, ref stack, OnAvailableItemConsumed, checkRecipeGroup)
				|| CheckContextItemCollection(context, GetModuleItems(context), reqType, ref stack, OnModuleItemConsumed, checkRecipeGroup);
		}

		private static CraftingContext CloneCraftingContextForValidation(CraftingContext context) => new() {
			sourceItems = context.sourceItems.Select(static item => item.Clone()).ToList(),
			availableItems = context.availableItems.Select(static item => item.Clone()).ToList(),
			toWithdraw = context.toWithdraw.Select(static item => item.Clone()).ToList(),
			results = context.results.Select(static item => item.Clone()).ToList(),
			itemCounts = new Dictionary<int, int>(context.itemCounts),
			sourceItemsFromModules = context.sourceItemsFromModules.Select(static item => item.Clone()).ToList(),
			sandbox = context.sandbox,
			consumedItemsFromModules = context.consumedItemsFromModules.Select(static item => item.Clone()).ToList(),
			moduleItemsToCommit = context.moduleItemsToCommit,
			modules = context.modules,
			toCraft = context.toCraft,
			simulation = true,
			recipe = context.recipe,
			player = context.player,
			heart = context.heart,
			environment = context.environment,
			availableRecipeObjects = context.availableRecipeObjects
		};

		private static IEnumerable<Item> GetAvailableItems(CraftingContext context) {
			for (int i = 0; i < context.availableItems.Count; i++)
				yield return context.availableItems[i];
		}

		private static void OnAvailableItemConsumed(CraftingContext context, int index, Item tryItem, int stackToConsume) {
			if (!context.simulation) {
				Item consumed = tryItem.Clone();
				consumed.stack = stackToConsume;

				context.toWithdraw.Add(consumed);
			}
		}

		private static IEnumerable<Item> GetModuleItems(CraftingContext context) {
			for (int i = 0; i < context.sourceItemsFromModules.Count; i++)
				yield return context.sourceItemsFromModules[i];
		}

		private static void OnModuleItemConsumed(CraftingContext context, int index, Item tryItem, int stackToConsume) {
			if (!context.simulation) {
				Item consumed = tryItem.Clone();
				consumed.stack = stackToConsume;

				context.consumedItemsFromModules.Add(consumed);
			}
		}

		private static bool CheckContextItemCollection(CraftingContext context, IEnumerable<Item> items, int reqType, ref int stack, Action<CraftingContext, int, Item, int> onItemConsumed, bool checkRecipeGroup = true) {
			int index = 0;
			foreach (Item tryItem in items) {
				// Recursion crafting can cause the item stack to be zero
				if (tryItem.stack <= 0)
					continue;

				if (reqType == tryItem.type || (checkRecipeGroup && RecipeGroupMatch(context.recipe, tryItem.type, reqType))) {
					int stackToConsume;

					if (tryItem.stack > stack) {
						stackToConsume = stack;
						stack = 0;
					} else {
						stackToConsume = tryItem.stack;
						stack -= tryItem.stack;
					}

					if (!context.simulation)
						OnConsumeItemForRecipe_Obsolete(context, tryItem, stackToConsume);

					onItemConsumed?.Invoke(context, index, tryItem, stackToConsume);

					tryItem.stack -= stackToConsume;

					if (tryItem.stack <= 0)
						tryItem.type = ItemID.None;

					if (stack <= 0)
						break;
				}

				index++;
			}

			return stack <= 0;
		}

		[Obsolete]
		private static void OnConsumeItemForRecipe_Obsolete(CraftingContext context, Item tryItem, int stackToConsume) {
			foreach (var module in context.modules)
				module.OnConsumeItemForRecipe(context.sandbox, tryItem, stackToConsume);
		}

		internal static List<Item> CompactItemList(List<Item> items) {
			List<Item> compacted = new();

			for (int i = 0; i < items.Count; i++) {
				Item item = items[i];

				if (item.IsAir)
					continue;

				bool fullyCompacted = false;
				for (int j = 0; j < compacted.Count; j++) {
					Item existing = compacted[j];

					if (StorageAggregator.CanCombineItems(item, existing)) {
						if (existing.stack + item.stack <= existing.maxStack) {
							Utility.CallOnStackHooks(existing, item, item.stack);

							existing.stack += item.stack;
							item.stack = 0;
							fullyCompacted = true;
						} else {
							int diff = existing.maxStack - existing.stack;

							Utility.CallOnStackHooks(existing, item, diff);

							existing.stack = existing.maxStack;
							item.stack -= diff;
						}

						break;
					}
				}

				if (!item.IsAir && !fullyCompacted)
					compacted.Add(item);
			}

			return compacted;
		}
	}
}
