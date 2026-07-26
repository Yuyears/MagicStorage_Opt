using MagicStorage.Common.Systems.RecurrentRecipes;
using System.Collections.Generic;
using System.Linq;
using System;
using Terraria.ModLoader;
using Terraria;
using MagicStorage.Components;
using Terraria.DataStructures;
using Terraria.ID;
using MagicStorage.CrossMod;
using MagicStorage.Common;
using MagicStorage.Common.Systems;
using Terraria.GameContent.Achievements;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MagicStorage {
	partial class CraftingGUI {
		internal const int MaxQueuedServerCrafts = 32;
		internal const int ServerCraftingWorkerCount = 4;
		private static readonly SemaphoreSlim ServerCraftingWorkerSlots = new(ServerCraftingWorkerCount, ServerCraftingWorkerCount);
		internal enum ServerCraftStage { Queued, Snapshot, Planning, Commit, Completed, Cancelled }

		internal sealed class ServerCraftRequest {
			private readonly CancellationTokenSource cancellation;
			private int stage;
			private int finished;

			public int Sender { get; }
			public long OperationId { get; }
			public TEStorageHeart Heart { get; }
			public TECraftingAccess Access { get; }
			public Recipe Recipe { get; }
			public int RequestedAmount { get; }
			public int RecursionDepth { get; }
			public int InventoryFingerprint { get; set; }
			public long QueuedAt { get; } = Stopwatch.GetTimestamp();
			public CancellationToken WorldToken { get; }
			public CancellationToken CancellationToken => cancellation.Token;
			public ServerCraftStage Stage => (ServerCraftStage)Volatile.Read(ref stage);
			public CraftRejectionReason RejectionReason { get; set; }

			public ServerCraftRequest(int sender, long operationId, TEStorageHeart heart, TECraftingAccess access, Recipe recipe, int requestedAmount, int recursionDepth) {
				Sender = sender;
				OperationId = operationId;
				Heart = heart;
				Access = access;
				Recipe = recipe;
				RequestedAmount = requestedAmount;
				RecursionDepth = recursionDepth;
				WorldToken = ServerActionsQueue.WorldCancellationToken;
				cancellation = CancellationTokenSource.CreateLinkedTokenSource(WorldToken);
			}

			public void SetStage(ServerCraftStage next) {
				int current;
				do {
					current = Volatile.Read(ref stage);
					if ((int)next <= current)
						return;
				} while (Interlocked.CompareExchange(ref stage, (int)next, current) != current);
			}

			public void Cancel() {
				SetStage(ServerCraftStage.Cancelled);
				cancellation.Cancel();
			}

			public bool TryFinish() => Interlocked.Exchange(ref finished, 1) == 0;
		}

		internal class CraftingContext {
			public List<Item> sourceItems, availableItems, toWithdraw, results;

			public Dictionary<int, int> itemCounts;

			public List<Item> sourceItemsFromModules;

			public EnvironmentSandbox sandbox;

			public List<Item> consumedItemsFromModules;

			public List<Item> moduleItemsToCommit;

			public IEnumerable<EnvironmentModule> modules;

			public int toCraft;

			public bool simulation;

			public Recipe recipe;

			public Player player;

			public TEStorageHeart heart;

			public CraftingInformation environment;

			public AvailableRecipeObjects availableRecipeObjects;

			public IEnumerable<Item> ConsumedItems => toWithdraw.Concat(consumedItemsFromModules);
		}

		internal static class RecipeEventHijack {
			public readonly record struct HijackArgs(int NetID, int Stack);

			public static HijackArgs? Args { get; private set; }

			public static void Invoke(Recipe recipe) => AchievementsHelper.NotifyItemCraft(recipe);

			public static void Invoke(Recipe recipe, HijackArgs args) {
				Args = args;
				Invoke(recipe);
				Args = null;
			}

			public static void Invoke(Recipe recipe, Item item) => Invoke(recipe, new HijackArgs(item.netID, item.stack));
		}

		/// <summary>
		/// Attempts to craft a certain amount of items from the currently assigned Crafting Interface.
		/// </summary>
		/// <param name="toCraft">How many items should be crafted</param>
		/// <returns><see langword="true"/> when at least one item was crafted or a multiplayer craft request was sent; otherwise, <see langword="false"/>.</returns>
		public static bool Craft(int toCraft) {
			TEStorageHeart heart = GetHeart();
			if (heart is null)
				return false;  // Bail

			NetHelper.Report(true, $"Attempting to craft {toCraft} {Lang.GetItemNameValue(selectedRecipe.createItem.type)}");

			if (Main.netMode == NetmodeID.MultiplayerClient) {
				TECraftingAccess access = GetCraftingEntity();
				if (access is null || toCraft <= 0)
					return false;

				toCraft = Math.Min(toCraft, Item.CommonMaxStack);
				NetHelper.Report(true, "Sending server-authoritative craft request...");
				return NetHelper.SendCraftRequest(access.Position, selectedRecipe.RecipeIndex, toCraft);
			}

			// Additional safeguard against absurdly high craft targets
			int origCraftRequest = toCraft;
			if (toCraft > (amountCraftableForCurrentRecipe ?? 0))
				amountCraftableForCurrentRecipe = null;

			toCraft = Math.Min(toCraft, AmountCraftableForCurrentRecipe());

			if (toCraft != origCraftRequest)
				NetHelper.Report(false, $"Craft amount reduced to {toCraft}");

			if (toCraft <= 0) {
				NetHelper.Report(false, "Amount to craft was less than 1, aborting");
				return false;
			}

			CraftingContext context;
			if (MagicStorageConfig.IsRecursionEnabled && selectedRecipe.HasRecursiveRecipe()) {
				// Recursive crafting uses special logic which can't just be injected into the previous logic
				context = Craft_WithRecursion(toCraft);

				if (context is null)
					return false;  // Bail

				if (context.toCraft >= toCraft) {
					NetHelper.Report(false, "Recursive crafting did not craft any items, aborting");
					return false;
				}

				if (context.results.Count <= 0) {
					NetHelper.Report(false, "Recursive crafting did not produce any results, aborting");
					return false;
				}
			} else {
				context = InitCraftingContext(selectedRecipe, toCraft);

				int target = toCraft;

				ExecuteInCraftingGuiEnvironment(context, Craft_DoStandardCraft);

				NetHelper.Report(true, $"Crafted {target - context.toCraft} items");

				if (target == context.toCraft) {
					//Could not craft anything, bail
					return false;
				}
			}

			NetHelper.Report(true, "Compacting results list...");

			context.toWithdraw = CompactItemList(context.toWithdraw);
			
			context.results = CompactItemList(context.results);

			foreach (Item result in context.results)
				RecipeEventHijack.Invoke(selectedRecipe, result);

			if (Main.netMode == NetmodeID.SinglePlayer) {
				NetHelper.Report(true, "Handling storage inventory changes and spawning excess results on player...");

				int producedStack = context.results.Sum(static item => item.stack);
				bool committed;
				List<Item> excessItems;
				using (SecuritySystem.CreateAccessContext()) {
					committed = TryHandleCraftWithdrawAndDeposit(heart, context.toWithdraw, [], [], context.results, out excessItems);
					foreach (Item item in excessItems)
						Main.LocalPlayer.QuickSpawnItem(new EntitySource_TileEntity(heart), item, item.stack);
				}

				int excessStack = excessItems.Sum(static item => item.stack);
				MagicStorageMod.Instance.Logger.Info($"Single-player craft recipe={selectedRecipe.RecipeIndex} ({Lang.GetItemNameValue(selectedRecipe.createItem.type)}), requested={toCraft}, committed={committed}, produced={producedStack}, deposited={(committed ? producedStack - excessStack : 0)}, excess={excessStack}");

				RequestRefreshAfterCraft(context);
				return true;
			}

			return false;
		}

		private static void InvokeOnCraft(Item item, Recipe recipe, List<Item> consumedItems) {
			try {
				RecipeLoader.OnCraft(item, recipe, consumedItems, new Item());
			} catch (Exception exception) when (Main.netMode == NetmodeID.Server) {
				MagicStorageMod.Instance.Logger.Warn($"Ignored a mod OnCraft callback failure for recipe {recipe.RecipeIndex}; the validated server craft will continue", exception);
			}
		}

		internal static bool TryCraftOnServer(Player player, TEStorageHeart heart, TECraftingAccess access, Recipe recipe, int requestedAmount, out List<Item> excessItems, out List<Item> producedItems, out List<Item> consumedItems) {
			Stopwatch timing = Stopwatch.StartNew();
			excessItems = [];
			producedItems = [];
			consumedItems = [];

			if (recipe is null || recipe.Disabled || recipe.createItem.IsAir || requestedAmount <= 0)
				return false;

			CraftingContext context = InitServerCraftingContext(player, heart, access, recipe, requestedAmount);
			bool available = false;
			ExecuteInCraftingEnvironment(player, context.environment, () => available = context.availableRecipeObjects.CanUseRecipe(recipe));
			if (!available) {
				MagicStorageMod.Instance.Logger.Info($"Rejected craft recipe={recipe.RecipeIndex}: station or condition unavailable ({timing.ElapsedMilliseconds}ms)");
				return false;
			}

			ExecuteInCraftingEnvironment(player, context.environment, () => {
				if (MagicStorageConfig.IsRecursionEnabled && recipe.TryGetRecursiveRecipe(out _))
					Craft_DoRecursionCraft(context);
				else
					Craft_DoStandardCraft(context);
			});

			if (context.toCraft > 0 || context.results.Count == 0) {
				MagicStorageMod.Instance.Logger.Info($"Rejected craft recipe={recipe.RecipeIndex}: produced={context.results.Count}, remaining={context.toCraft}/{requestedAmount} ({timing.ElapsedMilliseconds}ms)");
				return false;
			}

			context.toWithdraw = CompactItemList(context.toWithdraw);
			context.results = CompactItemList(context.results);
			context.consumedItemsFromModules = CompactItemList(context.consumedItemsFromModules);

			producedItems = context.results.Select(static item => item.Clone()).ToList();
			consumedItems = context.toWithdraw.Concat(context.consumedItemsFromModules).Select(static item => item.Clone()).ToList();
			bool committed = TryHandleCraftWithdrawAndDeposit(heart, context.toWithdraw, context.consumedItemsFromModules, context.moduleItemsToCommit, context.results, out excessItems);
			MagicStorageMod.Instance.Logger.Info($"Craft recipe={recipe.RecipeIndex}: committed={committed}, consumed={consumedItems.Count}, produced={producedItems.Count} ({timing.ElapsedMilliseconds}ms)");
			return committed;
		}

		internal static bool QueueCraftOnServer(int sender, long operationId, TEStorageHeart heart, TECraftingAccess access, Recipe recipe, int requestedAmount, int recursionDepth) {
			ServerCraftRequest request = new(sender, operationId, heart, access, recipe, requestedAmount, recursionDepth);
			bool queued = heart.TryQueueServerCraft(request);
			if (!queued)
				request.Cancel();
			return queued;
		}

		internal static void StartServerCraft(ServerCraftRequest request) {
			request.SetStage(ServerCraftStage.Snapshot);
			long snapshotStart = Stopwatch.GetTimestamp();
			double queueMs = Stopwatch.GetElapsedTime(request.QueuedAt, snapshotStart).TotalMilliseconds;
			try {
				if (!CanContinueServerCraft(request)) {
					request.RejectionReason = CraftRejectionReason.Cancelled;
					FinishServerCraft(request, false, [], [], []);
					return;
				}

				Player player = Main.player[request.Sender];
				RecursiveRecipe recursiveRecipe = null;
				bool recursive = request.RecursionDepth != 0 && request.Recipe.TryGetRecursiveRecipe(out recursiveRecipe);
				CraftingContext context;
				using (SecuritySystem.CreateAccessContext(request.Sender))
					context = InitServerCraftingContext(player, request.Heart, request.Access, request.Recipe, request.RequestedAmount, captureRecipeConditions: recursive);
				request.InventoryFingerprint = ComputeItemCountFingerprint(context.itemCounts);

				bool available = false;
				if (recursive)
					available = context.availableRecipeObjects.CanUseRecipe(request.Recipe);
				else
					ExecuteInCraftingEnvironment(player, context.environment, () => available = context.availableRecipeObjects.CanUseRecipe(request.Recipe));
				if (!available) {
					MagicStorageMod.Instance.Logger.Info($"Rejected craft operation={request.OperationId}, recipe={request.Recipe.RecipeIndex} ({Lang.GetItemNameValue(request.Recipe.createItem.type)}), requested={request.RequestedAmount}, reason=station-or-condition-unavailable, inventory={request.InventoryFingerprint:X8}");
					request.RejectionReason = CraftRejectionReason.Unavailable;
					FinishServerCraft(request, false, [], [], []);
					return;
				}

				double snapshotMs = Stopwatch.GetElapsedTime(snapshotStart).TotalMilliseconds;
				if (!recursive) {
					ExecuteInCraftingEnvironment(player, context.environment, () => Craft_DoStandardCraft(context));
					CompleteServerCraftOnMainThread(request, context, queueMs, snapshotMs, 0);
					return;
				}

				Task.Run(async () => {
					request.SetStage(ServerCraftStage.Planning);
					long planningStart = Stopwatch.GetTimestamp();
					try {
						await ServerCraftingWorkerSlots.WaitAsync(request.CancellationToken).ConfigureAwait(false);
						try {
							CraftingSimulation simulation = new();
							using (MagicStorageConfig.OverrideRecursionDepth(request.RecursionDepth)) {
								InventoryCraftabilityGraph graph = InventoryCraftabilityGraph.Build(
									context.availableRecipeObjects,
									MagicCache.EnabledRecipes,
									GetInventoryCraftabilityGraphDepth(),
									request.CancellationToken);
								if (!simulation.TryPlanCraftsWithGraph(recursiveRecipe, context.toCraft, context.availableRecipeObjects, graph, cancellationToken: request.CancellationToken))
									simulation.SimulateCrafts(recursiveRecipe, context.toCraft, context.availableRecipeObjects, cancellationToken: request.CancellationToken);
							}
							double planningMs = Stopwatch.GetElapsedTime(planningStart).TotalMilliseconds;
							QueueServerCraftCompletion(request, () => CompleteServerCraftOnMainThread(request, context, queueMs, snapshotMs, planningMs, simulation));
						} finally {
							ServerCraftingWorkerSlots.Release();
						}
					} catch (OperationCanceledException) {
						QueueServerCraftCompletion(request, () => {
							request.RejectionReason = CraftRejectionReason.Cancelled;
							FinishServerCraft(request, false, [], [], []);
						});
					} catch (Exception exception) {
						QueueServerCraftCompletion(request, () => {
							MagicStorageMod.Instance.Logger.Error($"Background craft planning failed for player {request.Sender}, recipe {request.Recipe.RecipeIndex}", exception);
							request.RejectionReason = CraftRejectionReason.InternalError;
							FinishServerCraft(request, false, [], [], []);
						});
					}
				});
			} catch (Exception exception) {
				MagicStorageMod.Instance.Logger.Error($"Queued craft preparation failed for player {request.Sender}, recipe {request.Recipe.RecipeIndex}", exception);
				request.RejectionReason = CraftRejectionReason.InternalError;
				FinishServerCraft(request, false, [], [], []);
			}
		}

		private static void QueueServerCraftCompletion(ServerCraftRequest request, Action action) {
			if (!ServerActionsQueue.QueueActionForWorld(action, request.WorldToken))
				request.Cancel();
		}

		private static void CompleteServerCraftOnMainThread(ServerCraftRequest request, CraftingContext context, double queueMs, double snapshotMs, double planningMs, CraftingSimulation simulation = null) {
			request.SetStage(ServerCraftStage.Commit);
			long commitStart = Stopwatch.GetTimestamp();
			if (!CanContinueServerCraft(request)) {
				request.RejectionReason = CraftRejectionReason.Cancelled;
				FinishServerCraft(request, false, [], [], []);
				return;
			}

			bool accepted;
			List<Item> excessItems;
			List<Item> producedItems;
			List<Item> consumedItems;
			try {
				using (SecuritySystem.CreateAccessContext(request.Sender)) {
					ExecuteInCraftingEnvironment(Main.player[request.Sender], context.environment, () => {
						if (simulation is null)
							return;
						Craft_DoRecursionCraft(context, simulation);
					});
					accepted = TryCommitServerCraft(context, out excessItems, out producedItems, out consumedItems);
				}
			} catch (Exception exception) {
				MagicStorageMod.Instance.Logger.Error($"Queued craft commit failed for player {request.Sender}, recipe {request.Recipe.RecipeIndex}", exception);
				request.RejectionReason = CraftRejectionReason.InternalError;
				accepted = false;
				excessItems = [];
				producedItems = [];
				consumedItems = [];
			}

			double commitMs = Stopwatch.GetElapsedTime(commitStart).TotalMilliseconds;
			if (!accepted && request.RejectionReason == CraftRejectionReason.None)
				request.RejectionReason = CraftRejectionReason.StateChanged;
			int producedStack = producedItems.Sum(static item => item.stack);
			int excessStack = excessItems.Sum(static item => item.stack);
			MagicStorageMod.Instance.Logger.Info($"Server craft operation={request.OperationId}, recipe={request.Recipe.RecipeIndex} ({Lang.GetItemNameValue(request.Recipe.createItem.type)}), requested={request.RequestedAmount}, recursionDepth={request.RecursionDepth}, accepted={(accepted ? request.RequestedAmount : 0)}, reason={(accepted ? CraftRejectionReason.None : request.RejectionReason)}, inventory={request.InventoryFingerprint:X8}, produced={producedStack}, deposited={producedStack - excessStack}, excess={excessStack}: queue={queueMs:F1}ms, snapshot={snapshotMs:F1}ms, planning={planningMs:F1}ms, commit={commitMs:F1}ms, total={Stopwatch.GetElapsedTime(request.QueuedAt).TotalMilliseconds:F1}ms");
			FinishServerCraft(request, accepted, excessItems, producedItems, consumedItems);
		}

		private static bool CanContinueServerCraft(ServerCraftRequest request)
			=> !request.CancellationToken.IsCancellationRequested
			&& request.Heart.IsAlive
			&& Main.netMode == NetmodeID.Server
			&& request.Sender >= 0
			&& request.Sender < Main.maxPlayers
			&& Main.player[request.Sender].active
			&& TileEntity.ByPosition.TryGetValue(request.Heart.Position, out TileEntity entity)
			&& ReferenceEquals(entity, request.Heart);

		private static void FinishServerCraft(ServerCraftRequest request, bool accepted, List<Item> excessItems, List<Item> producedItems, List<Item> consumedItems) {
			if (!request.TryFinish())
				return;
			if (!request.CancellationToken.IsCancellationRequested)
				request.SetStage(ServerCraftStage.Completed);
			try {
				NetHelper.CompleteCraftRequest(request.Sender, request.OperationId, request.Heart, accepted, request.RequestedAmount, request.RejectionReason, excessItems, producedItems, consumedItems);
			} catch (Exception exception) {
				MagicStorageMod.Instance.Logger.Error($"Craft completion fanout failed for player {request.Sender}, recipe {request.Recipe.RecipeIndex}", exception);
			} finally {
				request.Heart.CompleteServerCraft(request);
			}
		}

		internal static void CancelServerCraft(ServerCraftRequest request) {
			request.RejectionReason = CraftRejectionReason.Cancelled;
			request.Cancel();
			FinishServerCraft(request, false, [], [], []);
		}

		private static int ComputeItemCountFingerprint(IReadOnlyDictionary<int, int> itemCounts) {
			int hash = 17;
			foreach ((int type, int count) in itemCounts.OrderBy(static pair => pair.Key))
				unchecked { hash = (hash * 31 + type) * 31 + count; }
			return hash;
		}

		private static bool TryCommitServerCraft(CraftingContext context, out List<Item> excessItems, out List<Item> producedItems, out List<Item> consumedItems) {
			excessItems = [];
			producedItems = [];
			consumedItems = [];
			if (context.toCraft > 0 || context.results.Count == 0)
				return false;

			context.toWithdraw = CompactItemList(context.toWithdraw);
			context.results = CompactItemList(context.results);
			context.consumedItemsFromModules = CompactItemList(context.consumedItemsFromModules);
			producedItems = context.results.Select(static item => item.Clone()).ToList();
			consumedItems = context.toWithdraw.Concat(context.consumedItemsFromModules).Select(static item => item.Clone()).ToList();
			return TryHandleCraftWithdrawAndDeposit(context.heart, context.toWithdraw, context.consumedItemsFromModules, context.moduleItemsToCommit, context.results, out excessItems);
		}

		private static void RequestRefreshAfterCraft(CraftingContext context) {
			InvalidateSelectedRecipePreviewAfterInventoryChange();
			ForceNextRecipeRefreshToBeFull();
			RequestSelectedRecipeSnapshotForNextRecipeRefresh();
			PublishSelectedRecipeResultShell();
			MagicUI.RequestMainZoneThread();
		}

		private static void Craft_DoStandardCraft(CraftingContext context) {
			AttemptCraft(AttemptSingleCraft, context);
		}

		private static CraftingContext Craft_WithRecursion(int toCraft) {
			// Unlike normal crafting, the crafting tree has to be respected
			// This means that simple IsAvailable and AmountCraftable checks would just slow it down
			// Hence, the logic here will just assume that it's craftable and just ignore branches in the recursion tree that aren't available or are already satisfied
			if (!selectedRecipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe))
				throw new InvalidOperationException("Recipe object did not have a RecursiveRecipe object assigned to it");

			if (toCraft <= 0)
				return null;  // Bail

			CraftingContext context = InitCraftingContext(recursiveRecipe.original, toCraft);

			NetHelper.Report(true, "Attempting recurrent crafting...");

			// Local capturing
			var ctx = context;
			ExecuteInCraftingGuiEnvironment(ctx, Craft_DoRecursionCraft);

			// Sanity check
			return context;
		}

		private static void Craft_DoRecursionCraft(CraftingContext ctx) {
			Stopwatch timing = Stopwatch.StartNew();
			CraftingSimulation simulation;
			if (ctx.availableRecipeObjects is null)
				simulation = GetCraftingSimulationForCurrentRecipe(ctx.toCraft);
			else {
				simulation = new CraftingSimulation();
				simulation.SimulateCrafts(ctx.recipe.GetRecursiveRecipe(), ctx.toCraft, ctx.availableRecipeObjects);
			}

			MagicStorageMod.Instance.Logger.Info($"Recursive craft simulation recipe={ctx.recipe.RecipeIndex}: requested={ctx.toCraft}, crafted={simulation.AmountCrafted}, materials={simulation.RequiredMaterials.Count}, results={simulation.ExcessResults.Count} ({timing.ElapsedMilliseconds}ms)");
			Craft_DoRecursionCraft(ctx, simulation);
		}

		private static void Craft_DoRecursionCraft(CraftingContext ctx, CraftingSimulation simulation) {
			if (simulation.AmountCrafted <= 0) {
				NetHelper.Report(false, "Crafting simulation resulted in zero crafts, aborting");
				return;
			}

			// At this point, the amount to craft has already been clamped by the max amount possible
			// Hence, just consume the items
			List<Item> consumedItems = new();
			CraftingContext validation = CloneCraftingContextForValidation(ctx);

			validation.simulation = true;

			List<RequiredMaterialInfo> requiredMaterials = CloneRequiredMaterials(simulation.RequiredMaterials);
			foreach (var m in requiredMaterials) {
				if (m.Stack <= 0)
					continue;  // Safeguard: material was already "used up" by higher up recipes

				var material = m;

				List<Item> origWithdraw = new(validation.toWithdraw);
				List<Item> origResults = new(validation.results);
				List<Item> origFromModule = new(validation.consumedItemsFromModules);

				bool skipItemConsumption = false;

				foreach (int type in material.GetValidItems()) {
					// Bug fix: only consume up to the amount of materials needed
					if (!validation.itemCounts.TryGetValue(type, out int quantity) || quantity <= 0) {
						// Item was not present
						continue;
					}

					int possibleStack = Math.Min(material.Stack, quantity);

					Item item = new Item(type, possibleStack);

					if (!CanConsumeItem(validation, item, origWithdraw, origResults, origFromModule, out bool wasAvailable, out int stackConsumed, checkRecipeGroup: false)) {
						if (wasAvailable) {
							NetHelper.Report(false, $"Skipping consumption of item \"{Lang.GetItemNameValue(item.type)}\"");
							skipItemConsumption = true;
							break;
						}
					} else {
						// FIX: v0.7.0.9 - Some recipes (e.g. Alchemy Table) can reduce the ingredient requirement during crafting.  This needs to be respected.
						if (item.stack < possibleStack)
							material.UpdateStack(item.stack - possibleStack);

						// Consume the item
						material.UpdateStack(-stackConsumed);
						item.stack = stackConsumed;
						consumedItems.Add(item);

						validation.itemCounts[type] -= stackConsumed;

						if (material.Stack <= 0)
							break;
					}
				}

				if (!skipItemConsumption && material.Stack > 0) {
					NetHelper.Report(false, $"Material requirement \"{Lang.GetItemNameValue(material.GetValidItems().First())}\" could not be met, aborting");
					return;
				}
			}

			NetHelper.Report(true, $"Recursion crafting used the following materials:\n  {
				(consumedItems.Count > 0
					? string.Join("\n  ", consumedItems.Select(static i => $"{i.stack} {Lang.GetItemNameValue(i.type)}"))
					: "none")
				}");

			// Actually consume the items
			foreach (Item item in consumedItems) {
				int stack = item.stack;
				if (!AttemptToConsumeItem(ctx, item.type, ref stack, checkRecipeGroup: false) || stack > 0) {
					NetHelper.Report(false, "Validated recursive ingredient plan could not be applied, aborting");
					return;
				}
			}

			// Run the "on craft" logic for the final result, but with the SimulatingCrafts flag disabled this time
			// (It should be false by this point, but it's forced back to false as a sanity check)
			_simulatingCrafts = false;

			// Inform other mods that the items were crafted
			using (FlagSwitch.ToggleTrue(ref CatchDroppedItems)) {
				DroppedItems ??= new();
				DroppedItems.Clear();
				foreach (Item item in simulation.ExcessResults.Where(static i => i.Stack > 0).Select(static i => new Item(i.type, i.Stack, i.prefix))) {
					item.Prefix(-1);
					ctx.results.Add(item);
				}

				foreach (RecursedRecipe operation in simulation.CraftOperations) {
					for (int batch = 0; batch < operation.batches; batch++) {
						foreach (EnvironmentModule module in ctx.modules)
							module.OnConsumeItemsForRecipe(ctx.sandbox, operation.recipe, consumedItems);

						foreach (Item item in ExtraCraftItemsSystem.GetSimulatedItemDrops(operation.recipe)) {
							ctx.results.Add(item);
							InvokeOnCraft(item, operation.recipe, consumedItems);
						}

						Item craftedItem = operation.recipe.createItem.Clone();
						craftedItem.Prefix(-1);
						InvokeOnCraft(craftedItem, operation.recipe, consumedItems);
					}
				}
			}

			ctx.toCraft -= simulation.AmountCrafted;

			NetHelper.Report(true, $"Success! Crafted {simulation.AmountCrafted} items and {simulation.ExcessResults.Count - 1} extra item types");
		}

		private static List<RequiredMaterialInfo> CloneRequiredMaterials(IReadOnlyList<RequiredMaterialInfo> materials) {
			List<RequiredMaterialInfo> result = new(materials.Count);

			foreach (RequiredMaterialInfo material in materials) {
				SharedCounter stack = new(material.Stack);
				result.Add(material.recipeGroup
					? RequiredMaterialInfo.FromGroup(material.itemOrGroupID, stack)
					: RequiredMaterialInfo.FromItem(material.itemOrGroupID, stack));
			}

			return result;
		}

		private static void AttemptCraft(Func<CraftingContext, bool> func, CraftingContext context) {
			// NOTE: [ThreadStatic] only runs the field initializer on one thread
			DroppedItems ??= new();

			List<Item> consumedItems = new();

			while (context.toCraft > 0) {
				if (!func(context))
					break;  // Could not craft any more items

				Item resultItem = context.recipe.createItem.Clone();
				context.toCraft -= resultItem.stack;

				resultItem.Prefix(-1);
				context.results.Add(resultItem);

				consumedItems = context.ConsumedItems.ToList();

				foreach (EnvironmentModule module in context.modules)
					module.OnConsumeItemsForRecipe(context.sandbox, context.recipe, consumedItems);

				// Inform other mods that the items were crafted
				using (FlagSwitch.ToggleTrue(ref CatchDroppedItems)) {
					foreach (Item item in ExtraCraftItemsSystem.GetSimulatedItemDrops(context.recipe)) {
						context.results.Add(item);
						InvokeOnCraft(item, context.recipe, consumedItems);
					}

					DroppedItems.Clear();
					InvokeOnCraft(resultItem, context.recipe, consumedItems);
				}
			}
		}

		private static bool AttemptLazyBatchCraft(CraftingContext context) {
			NetHelper.Report(false, "Attempting batch craft operation...");

			List<Item> origResults = new(context.results);
			List<Item> origWithdraw = new(context.toWithdraw);
			List<Item> origFromModule = new(context.consumedItemsFromModules);

			//Try to batch as many "crafts" into one craft as possible
			int crafts = (int)Math.Ceiling(context.toCraft / (float)context.recipe.createItem.stack);

			//Skip item consumption code for recipes that have no ingredients
			if (context.recipe.requiredItem.Count == 0) {
				NetHelper.Report(false, "Recipe had no ingredients, skipping consumption...");
				goto SkipItemConsumption;
			}

			context.simulation = true;

			List<Item> batch = new(context.recipe.requiredItem.Count);

			//Reduce the number of batch crafts until this recipe can be completely batched for the number of crafts
			while (crafts > 0) {
				bool didAttemptToConsumeItem = false;

				foreach (Item reqItem in context.recipe.requiredItem) {
					Item clone = reqItem.Clone();
					clone.stack *= crafts;

					if (!CanConsumeItem(context, clone, origWithdraw, origResults, origFromModule, out bool wasAvailable, out int stackConsumed)) {
						if (wasAvailable) {
							NetHelper.Report(false, $"Skipping consumption of item \"{Lang.GetItemNameValue(reqItem.type)}\". (Batching {crafts} crafts)");

							// Indicate to later logic that an attempt was made
							didAttemptToConsumeItem = true;
						} else {
							// Did not have enough items
							crafts--;
							batch.Clear();
							didAttemptToConsumeItem = false;
							break;
						}
					} else {
						//Consume the item
						clone.stack = stackConsumed;
						batch.Add(clone);
					}
				}

				if (batch.Count > 0 || didAttemptToConsumeItem) {
					//Successfully batched items for the craft
					break;
				}
			}

			// Remove any empty items since they wouldn't do anything anyway
			batch.RemoveAll(i => i.stack <= 0);

			context.simulation = false;

			if (crafts <= 0) {
				//Craft batching failed
				return false;
			}

			//Consume the batched items
			foreach (Item item in batch) {
				int stack = item.stack;

				AttemptToConsumeItem(context, item.type, ref stack);
			}

			NetHelper.Report(true, $"Batch crafting used the following materials:\n  {string.Join("\n  ", batch.Select(static i => $"{i.stack} {Lang.GetItemNameValue(i.type)}"))}");

			SkipItemConsumption:

			// NOTE: [ThreadStatic] only runs the field initializer on one thread
			DroppedItems ??= new();

			//Create the resulting items
			List<Item> consumedItems = context.ConsumedItems.ToList();

			for (int i = 0; i < crafts; i++) {
				Item resultItem = context.recipe.createItem.Clone();
				context.toCraft -= resultItem.stack;

				resultItem.Prefix(-1);
				context.results.Add(resultItem);

				foreach (EnvironmentModule module in context.modules)
					module.OnConsumeItemsForRecipe(context.sandbox, context.recipe, consumedItems);

				// Inform other mods that the items were crafted
				using (FlagSwitch.ToggleTrue(ref CatchDroppedItems)) {
					foreach (Item item in ExtraCraftItemsSystem.GetSimulatedItemDrops(context.recipe)) {
						context.results.Add(item);
						InvokeOnCraft(item, context.recipe, consumedItems);
					}

					DroppedItems.Clear();
					InvokeOnCraft(resultItem, context.recipe, consumedItems);
				}
			}

			NetHelper.Report(false, $"Batch craft operation succeeded ({crafts} crafts batched)");

			return true;
		}

		private static bool AttemptSingleCraft(CraftingContext context) {
			NetHelper.Report(false, "Attempting one craft operation...");

			CraftingContext validation = CloneCraftingContextForValidation(context);

			List<int> stacksConsumed = new();

			foreach (Item reqItem in context.recipe.requiredItem) {
				Item ingredient = reqItem.Clone();
				List<Item> origResults = new(validation.results);
				List<Item> origWithdraw = new(validation.toWithdraw);
				List<Item> origFromModule = new(validation.consumedItemsFromModules);
				if (!CanConsumeItem(validation, ingredient, origWithdraw, origResults, origFromModule, out bool wasAvailable, out int stackConsumed)) {
					if (wasAvailable)
						NetHelper.Report(false, $"Skipping consumption of item \"{Lang.GetItemNameValue(reqItem.type)}\".");
					else {
						NetHelper.Report(false, $"Required item \"{Lang.GetItemNameValue(reqItem.type)}\" was not available.");
						return false;  // Did not have enough items
					}
				} else
					NetHelper.Report(false, $"Required item \"{Lang.GetItemNameValue(reqItem.type)}\" was available.");

				stacksConsumed.Add(stackConsumed);
			}

			// Apply the already validated plan to the request-local inventory snapshot.
			int consumeStackIndex = 0;
			foreach (Item reqItem in context.recipe.requiredItem) {
				int stack = stacksConsumed[consumeStackIndex];
				if (stack > 0 && (!AttemptToConsumeItem(context, reqItem.type, ref stack) || stack > 0)) {
					NetHelper.Report(false, "Validated ingredient plan could not be applied, aborting");
					return false;
				}
				consumeStackIndex++;
			}

			NetHelper.Report(false, "Craft operation succeeded");

			return true;
		}
	}
}
