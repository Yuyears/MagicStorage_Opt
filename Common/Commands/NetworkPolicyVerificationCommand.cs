using System;
using MagicStorage.Common.Players;
using MagicStorage.Common.Systems;
using MagicStorage.Common.Systems.RecurrentRecipes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MagicStorage.Common.Systems.Auditing;
using MagicStorage.Common.Systems.Shimmering;
using MagicStorage.Common.Threading;
using MagicStorage.Common.Threading.Refreshing;
using MagicStorage.Components;
using MagicStorage.Sorting;
using MagicStorage.UI;

namespace MagicStorage.Common.Commands {
	internal sealed class NetworkPolicyVerificationCommand : ModCommand {
		public override CommandType Type => CommandType.Chat;

		public override string Command => "msverifynetpolicy";

		public override string Usage => "/msverifynetpolicy";

		public override string Description => "Runs deterministic inbound packet policy checks.";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 0) {
				caller.Reply($"Usage: {Usage}", Color.Red);
				return;
			}

			foreach (MessageType type in Enum.GetValues<MessageType>()) {
				PacketDirection direction = InboundPacketGuard.GetDirection(type);
				Require(direction != PacketDirection.None, $"{type} has no direction policy.");
				Require(InboundPacketGuard.IsDirectionAllowed(type, NetmodeID.Server) == direction.HasFlag(PacketDirection.ClientToServer), $"{type} server direction mismatch.");
				Require(InboundPacketGuard.IsDirectionAllowed(type, NetmodeID.MultiplayerClient) == direction.HasFlag(PacketDirection.ServerToClient), $"{type} client direction mismatch.");
			}

			Require(!InboundPacketGuard.IsDirectionAllowed((MessageType)byte.MaxValue, NetmodeID.Server), "Unknown packet type was accepted.");
			Require(!InboundPacketGuard.IsDirectionAllowed(MessageType.PlayerHasServerOp, NetmodeID.Server), "Client-authored operator state was accepted.");
			Require(!InboundPacketGuard.IsDirectionAllowed(MessageType.SecurityPlayerSync, NetmodeID.Server), "Client-authored security identity was accepted.");
			Require(InboundPacketGuard.GetDirection(MessageType.SecurityNetworkPassword) == PacketDirection.ClientToServer, "Password requests can return credentials to clients.");
			Require(InboundPacketGuard.GetDirection(MessageType.StorageHeartNetwork) == PacketDirection.ServerToClient, "Clients can directly assign component networks.");
			Require(InboundPacketGuard.GetDirection(MessageType.ClientStorageComponentOperation) == PacketDirection.ClientToServer, "Storage component operations were not client-to-server only.");
			Require(Enum.GetValues<StorageComponentOperation>().Length == 2, "Unexpected storage component operation was exposed.");
			Require(NetHelper.GetStorageComponentOperationPayloadLength(StorageComponentOperation.RemoteAccessLink) == 8, "Remote access link payload length changed.");
			Require(NetHelper.GetStorageComponentOperationPayloadLength(StorageComponentOperation.EnvironmentModuleToggle) == 9, "Environment toggle payload length changed.");
			Require(NetHelper.GetStorageComponentOperationPayloadLength((StorageComponentOperation)byte.MaxValue) < 0, "Unknown storage component operation was accepted.");
			Require(InboundPacketGuard.GetDirection(MessageType.ShimmerItemInStorageResult) == PacketDirection.ServerToClient, "Clients can forge shimmer completion responses.");
			Require(InboundPacketGuard.GetDirection(MessageType.RequestShimmerItemInStorage) == PacketDirection.ClientToServer, "Shimmer requests were not client-to-server only.");
			Require(InboundPacketGuard.GetDirection(MessageType.CraftOutcome) == PacketDirection.ServerToClient, "Clients can forge authoritative craft outcomes.");
			Require(Enum.GetValues<TECraftingAccess.Operation>().Length == 4, "Unexpected crafting station operation was exposed.");
			Require(Enum.GetValues<PlayerBankInventory>().Length == 4, "Unexpected player bank inventory was exposed.");
			Require(InboundPacketGuard.IsValidInventorySlot(0) && InboundPacketGuard.IsValidInventorySlot(58), "Valid player inventory slots were rejected.");
			Require(!InboundPacketGuard.IsValidInventorySlot(-1) && !InboundPacketGuard.IsValidInventorySlot(59), "Invalid player inventory slots were accepted.");
			Require(TECraftingAccess.DepositInventorySlot == PlayerItemSlotID.InventoryMouseItem, "Station deposits did not use the synchronized mouse-item slot.");
			Require(TECraftingAccess.IsValidDepositSourceSlot(PlayerItemSlotID.InventoryMouseItem) && !TECraftingAccess.IsValidDepositSourceSlot(0), "Station deposit source-slot validation was incorrect.");
			Require(!TECraftingAccess.IsValidStationItem(new Item()) && TECraftingAccess.IsValidStationItem(new Item(ItemID.WorkBench)), "Station item validation accepted an empty item or rejected a valid station.");
			Require(InboundPacketGuard.MaxSerializedItemBytes > 0, "Serialized item payload limit is invalid.");
			Require(InboundPacketGuard.MaxStorageNameLength > 0, "Storage name length limit is invalid.");
			using (MemoryStream stream = new()) {
				using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
					writer.Write(new string('a', InboundPacketGuard.MaxStorageNameLength));
				stream.Position = 0;
				using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
				Require(NetHelper.TryReadBoundedString(reader, InboundPacketGuard.MaxStorageNameLength, out string value) && value.Length == InboundPacketGuard.MaxStorageNameLength, "Bounded storage name did not round-trip.");
			}
			using (MemoryStream stream = new()) {
				using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
					writer.Write(new string('a', InboundPacketGuard.MaxStorageNameLength + 1));
				stream.Position = 0;
				using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
				Require(!NetHelper.TryReadBoundedString(reader, InboundPacketGuard.MaxStorageNameLength, out _), "Oversized storage name was accepted.");
			}
			using (BinaryReader reader = new(new MemoryStream([0x80])))
				Require(!NetHelper.TryReadBoundedString(reader, InboundPacketGuard.MaxStorageNameLength, out _), "Truncated storage name was accepted.");
			Require(NetHelper.CraftRequestPayloadLength == 41, "Craft request payload is not fixed-width.");
			byte[] craftPayload;
			using (MemoryStream stream = new()) {
				Point16 position = new(123, -45);
				const long operationId = 456789;
				const ulong fingerprint = 123456789UL;
				const int recipeCount = 2468;
				const ulong tableDigest = 987654321UL;
				using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
					NetHelper.WriteCraftRequestPayload(writer, position, operationId, 678, 9, 4, fingerprint, recipeCount, tableDigest);
				Require(stream.Length == NetHelper.CraftRequestPayloadLength, "Craft request writer produced an unexpected payload length.");

				stream.Position = 0;
				using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
				NetHelper.ReadCraftRequestPayload(reader, out Point16 readPosition, out long readOperationId, out int readRecipeIndex, out int readRequestedAmount, out int readRecursionDepth, out ulong readFingerprint, out int readRecipeCount, out ulong readTableDigest);
				Require(readPosition == position && readOperationId == operationId && readRecipeIndex == 678 && readRequestedAmount == 9 && readRecursionDepth == 4 && readFingerprint == fingerprint && readRecipeCount == recipeCount && readTableDigest == tableDigest, "Craft request payload did not round-trip.");
				Require(stream.Position == stream.Length, "Craft request reader did not consume the complete payload.");
				craftPayload = stream.ToArray();
			}
			for (int length = 0; length < craftPayload.Length; length++) {
				using BinaryReader reader = new(new MemoryStream(craftPayload, 0, length, writable: false));
				Require(!NetHelper.TryReadCraftRequestPayload(reader, out _, out _, out _, out _, out _, out _, out _, out _), $"Truncated craft payload length {length} was accepted.");
			}
			using (BinaryReader reader = new(new MemoryStream()))
				Require(!NetHelper.TryHandlePacket(reader, caller.Player.whoAmI), "Empty packet escaped malformed-packet rejection.");
			using (BinaryReader reader = new(new MemoryStream([(byte)MessageType.RenameStorageHeart])))
				Require(!NetHelper.TryHandlePacket(reader, caller.Player.whoAmI), "Truncated packet escaped malformed-packet rejection.");
			Require(NetHelper.IsValidCraftRequest(0, 1, -1, 1), "Valid infinite-recursion craft request was rejected.");
			Require(NetHelper.IsValidCraftRequest(0, 1, 0, 1) && NetHelper.IsValidCraftRequest(0, 1, 10, 1), "Valid finite recursion depth was rejected.");
			Require(!NetHelper.IsValidCraftRequest(-1, 1, 0, 1), "Negative recipe index was accepted.");
			Require(!NetHelper.IsValidCraftRequest(1, 1, 0, 1), "Out-of-range recipe index was accepted.");
			Require(!NetHelper.IsValidCraftRequest(0, 0, 0, 1), "Zero craft amount was accepted.");
			Require(!NetHelper.IsValidCraftRequest(0, Item.CommonMaxStack + 1, 0, 1), "Oversized craft amount was accepted.");
			Require(!NetHelper.IsValidCraftRequest(0, 1, -2, 1) && !NetHelper.IsValidCraftRequest(0, 1, 11, 1), "Out-of-range recursion depth was accepted.");
			int configuredRecursionDepth = MagicStorageConfig.RecipeRecursionDepth;
			using (MagicStorageConfig.OverrideRecursionDepth(4)) {
				Require(MagicStorageConfig.RecipeRecursionDepth == 4, "Request recursion depth did not override the process configuration.");
				using (MagicStorageConfig.OverrideRecursionDepth(-1))
					Require(MagicStorageConfig.IsRecursionInfinite, "Nested infinite-recursion override was ignored.");
				Require(MagicStorageConfig.RecipeRecursionDepth == 4, "Nested recursion override did not restore its parent value.");
			}
			Require(MagicStorageConfig.RecipeRecursionDepth == configuredRecursionDepth, "Request recursion override leaked after disposal.");
			Require(CraftingGUI.MaxQueuedServerCrafts > 0 && CraftingGUI.MaxQueuedServerCrafts <= 32, "Server craft queue limit is invalid.");
			Require(CraftingGUI.ServerCraftingWorkerCount > 0 && CraftingGUI.ServerCraftingWorkerCount <= 8, "Server craft worker limit is invalid.");
			Require(RefreshParallelism.WorkerCount > 0 && RefreshParallelism.WorkerCount <= RefreshParallelism.DefaultWorkerCount && RefreshParallelism.MaxWorkerCount == 8, "Refresh worker bounds are invalid.");
			List<Item> countInput = [new Item(ItemID.Wood, 2), new Item(ItemID.Wood, 5) { prefix = 1 }, new Item(ItemID.StoneBlock, 3)];
			Dictionary<int, int> singleCounts = [], parallelCounts = [];
			Dictionary<int, Dictionary<int, int>> singlePrefixes = [], parallelPrefixes = [];
			CraftingGUI.BuildItemCounts(countInput, [], singleCounts, singlePrefixes, 1, minimumParallelWorkItems: 0);
			CraftingGUI.BuildItemCounts(countInput, [], parallelCounts, parallelPrefixes, 8, minimumParallelWorkItems: 0);
			Require(singleCounts.Count == parallelCounts.Count && singleCounts.All(pair => parallelCounts.GetValueOrDefault(pair.Key) == pair.Value), "Parallel item counting changed type totals.");
			Require(parallelCounts.GetValueOrDefault(ItemID.Wood) == 7 && parallelPrefixes[ItemID.Wood].GetValueOrDefault(0) == 2 && parallelPrefixes[ItemID.Wood].GetValueOrDefault(1) == 5, "Parallel item counting changed prefix totals.");
			ItemAggregateResults singleItemAggregate = new([new Item(ItemID.Wood)]);
			int aggregateProgress = 0;
			singleItemAggregate.Aggregate(default, uniqueSlotPerItemStack: false, ref aggregateProgress);
			Require(singleItemAggregate.SourceCount == 1, "Item aggregation omitted the first source stack.");
			ItemAggregateResults compactAggregate = new([new Item(ItemID.Wood, 2), new Item(ItemID.Wood, 3)]) { RetainSourceGroups = false };
			compactAggregate.Aggregate(default, uniqueSlotPerItemStack: false, ref aggregateProgress);
			List<List<Item>> movedAggregateGroups = [];
			compactAggregate.MoveResultGroupsTo(movedAggregateGroups);
			Require(compactAggregate.SourceCount == 2 && !compactAggregate.GetSourceGroups().Any() && movedAggregateGroups.Count == 1 && movedAggregateGroups[0].Count == 1 && movedAggregateGroups[0][0].stack == 5, "Compact aggregation changed result groups or retained source groups.");
			Require(compactAggregate.GetResultItems().Single().stack == 5 && !compactAggregate.GetResultItemGroups().Any(), "Moving aggregation result groups changed result items or retained moved groups.");
			Require(TEStorageHeart.NetworkOperationTimeoutMilliseconds > 800, "Network operation timeout does not tolerate 800ms latency.");
			Require(TEStorageHeart.CraftOperationTimeoutMilliseconds > TEStorageHeart.NetworkOperationTimeoutMilliseconds, "Long-running server crafts use the ordinary storage timeout.");
			Require(TEStorageHeart.NetworkWarningCooldownMilliseconds >= TEStorageHeart.NetworkOperationTimeoutMilliseconds, "Network warning cooldown is shorter than the operation timeout.");
			long timeoutTicks = (long)(Stopwatch.Frequency * TEStorageHeart.NetworkOperationTimeoutMilliseconds / 1000d);
			long toleratedTicks = (long)(Stopwatch.Frequency * 800 / 1000d);
			Require(!TEStorageHeart.IsNetworkOperationTimedOut(0, toleratedTicks), "An 800ms operation was treated as timed out.");
			Require(TEStorageHeart.IsNetworkOperationTimedOut(0, timeoutTicks), "The configured operation timeout boundary was not enforced.");
			Require(TEStorageHeart.ShouldAcceptNetworkRevision(10, 10) && TEStorageHeart.ShouldAcceptNetworkRevision(10, 11), "Current or newer network revisions were rejected.");
			Require(!TEStorageHeart.ShouldAcceptNetworkRevision(10, 9), "An older network revision was accepted.");
			Require(TEStorageHeart.IsStorageSnapshotCurrent(4, 8, 4, 8), "An unchanged storage snapshot was rejected.");
			Require(!TEStorageHeart.IsStorageSnapshotCurrent(4, 8, 5, 8) && !TEStorageHeart.IsStorageSnapshotCurrent(4, 8, 4, 9), "A changed storage snapshot was accepted.");
			List<Item> liveItems = [new Item(ItemID.Wood, 5)];
			Item[] immutableSnapshot = TEStorageUnit.CloneItemsForSnapshot(liveItems);
			liveItems[0].stack = 1;
			Require(immutableSnapshot.Length == 1 && immutableSnapshot[0].stack == 5 && !ReferenceEquals(liveItems[0], immutableSnapshot[0]), "Storage snapshot retained a mutable live item reference.");
			TEStorageUnit unitWithUnpublishedContents = new();
			unitWithUnpublishedContents.items.Add(new Item(ItemID.Wood, 5));
			StorageUnitSnapshot publishedSnapshot = unitWithUnpublishedContents.GetItemSnapshot();
			Require(publishedSnapshot.Items.Count == 1 && publishedSnapshot.Items[0].stack == 5 && !ReferenceEquals(unitWithUnpublishedContents.items[0], publishedSnapshot.Items[0]), "Storage unit did not initialize an immutable snapshot for existing contents.");
			object firstUnit = new(), indexedUnit = new(), lastUnit = new(), staleUnit = new();
			List<object> allUnits = [firstUnit, indexedUnit, lastUnit];
			List<object> routedUnits = [.. TEStorageHeart.EnumerateIndexedCandidatesWithFallback([indexedUnit, staleUnit, indexedUnit], allUnits, new HashSet<object>(allUnits))];
			Require(routedUnits.Count == 3 && ReferenceEquals(routedUnits[0], indexedUnit) && ReferenceEquals(routedUnits[1], firstUnit) && ReferenceEquals(routedUnits[2], lastUnit), "Storage routing candidates did not preserve indexed priority, fallback order, de-duplication, and stale-reference rejection.");
			object compactFirst = new(), compactSecond = new(), compactThird = new();
			List<(int Index, object Unit)> compactDestinations = [(0, compactFirst), (2, compactSecond), (4, compactThird)];
			List<(int Index, object Unit)> compactSources = [(0, compactFirst), (1, new object()), (2, compactSecond), (3, new object()), (4, compactThird)];
			List<(object Destination, object Source)> compactPairs = [.. TEStorageHeart.EnumerateOrderedCompactionCandidates(compactDestinations, compactSources)];
			Require(compactPairs.Count == 6
				&& ReferenceEquals(compactPairs[0].Destination, compactFirst) && ReferenceEquals(compactPairs[0].Source, compactSources[1].Unit)
				&& ReferenceEquals(compactPairs[1].Destination, compactFirst) && ReferenceEquals(compactPairs[1].Source, compactSecond)
				&& ReferenceEquals(compactPairs[2].Destination, compactFirst) && ReferenceEquals(compactPairs[2].Source, compactSources[3].Unit)
				&& ReferenceEquals(compactPairs[3].Destination, compactFirst) && ReferenceEquals(compactPairs[3].Source, compactThird)
				&& ReferenceEquals(compactPairs[4].Destination, compactSecond) && ReferenceEquals(compactPairs[4].Source, compactSources[3].Unit)
				&& ReferenceEquals(compactPairs[5].Destination, compactSecond) && ReferenceEquals(compactPairs[5].Source, compactThird), "Compaction candidates changed source ordering or included a unit itself.");
			TEStorageHeart pendingHeart = new();
			long pendingCraft = pendingHeart.BeginClientOperation(TEStorageHeart.PendingOperationKind.Craft);
			Require(pendingHeart.HasPendingOperation(TEStorageHeart.PendingOperationKind.Craft), "Craft operation was not registered as pending.");
			Require(!pendingHeart.CompleteClientOperation(pendingCraft + 1), "An unrelated operation ID cleared the pending craft.");
			Require(pendingHeart.CompleteClientOperation(pendingCraft) && !pendingHeart.HasPendingOperation(TEStorageHeart.PendingOperationKind.Craft), "The matching operation ID did not clear the pending craft.");
			List<Item> unfiltered = [new Item(ItemID.DirtBlock)];
			CraftingGUI.CopyUnfilteredItems(unfiltered, [new Item(ItemID.Wood)], [new Item(ItemID.StoneBlock)]);
			Require(unfiltered.Count == 2 && unfiltered[0].type == ItemID.Wood && unfiltered[1].type == ItemID.StoneBlock, "Unfiltered crafting inventory omitted storage or module items.");
			Item firstResultStack = new(ItemID.Wood, 3);
			Item aggregatedResult = CraftingGUI.AggregateCompatibleResultItem(null, firstResultStack);
			aggregatedResult = CraftingGUI.AggregateCompatibleResultItem(aggregatedResult, new Item(ItemID.Wood, 4));
			Require(aggregatedResult.stack == 7 && firstResultStack.stack == 3, "Crafting result total did not aggregate compatible stacks without mutating its source snapshot.");
			Item prefixedResult = new(ItemID.IronBroadsword) { prefix = 1 };
			Item incompatibleResult = new(ItemID.IronBroadsword) { prefix = 2 };
			Item retainedResult = CraftingGUI.AggregateCompatibleResultItem(prefixedResult.Clone(), incompatibleResult);
			Require(retainedResult.stack == 1 && retainedResult.prefix == prefixedResult.prefix, "Crafting result total merged incompatible item variants.");
			ShimmerItemReports shimmerReports = new([new ItemReport(ItemID.Wood)]);
			shimmerReports.CopyFromStaticCollection();
			shimmerReports.ReplaceReports([new ItemReport(ItemID.StoneBlock)]);
			Require(shimmerReports.reports.Count == 1 && shimmerReports.reports[0].Equals(new ItemReport(ItemID.StoneBlock)), "Selected shimmer reports retained the previous item.");
			Item nearlyFullCursor = new(ItemID.Wood, 998) { maxStack = 999 };
			foreach (int increment in new[] { 1, 5, 10, 50, 9999 })
				Require(ItemStackSplitting.ClampSplitAmount(nearlyFullCursor, increment) == 1, $"Quick split increment {increment} exceeded cursor capacity.");
			ItemSorter.ClearTaskNameCache();
			string itemTask = ItemSorter.SortAndFilter_GenerateTaskName("Filtering", 0, "Items", "Recipes");
			string recipeTask = ItemSorter.SortAndFilter_GenerateTaskName("Filtering", 0, "Recipes", "Recipes");
			Require(itemTask == "Filtering Recipes Items" && recipeTask == "Filtering Recipes Recipes", "Sorting task-name cache cross-wired collection keys.");
			ItemSorter.ClearTaskNameCache();
			Require((byte)MessageType.ClientStorageOperation == 1, "Corrected storage operation name changed its wire value.");
			Require(typeof(IStorageItemsProvider).IsInterface && typeof(IWriteInterceptor).IsInterface, "Corrected provider/interceptor symbols are unavailable.");
			Recipe repeatedIngredientRecipe = new();
			repeatedIngredientRecipe.createItem.SetDefaults(ItemID.WorkBench);
			repeatedIngredientRecipe.requiredItem.Add(new Item(ItemID.Wood, 1));
			repeatedIngredientRecipe.requiredItem.Add(new Item(ItemID.Wood, 1));
			repeatedIngredientRecipe.acceptedGroups.Add(RecipeGroupID.Wood);
			Require(!CraftingGUI.CanReserveRecipeBatches(repeatedIngredientRecipe, new Dictionary<int, int> { [ItemID.BorealWood] = 1 }, [], 1), "Repeated grouped ingredients reused one item stack.");
			Require(CraftingGUI.CanReserveRecipeBatches(repeatedIngredientRecipe, new Dictionary<int, int> { [ItemID.BorealWood] = 2 }, [], 1), "Repeated grouped ingredients rejected sufficient inventory.");
			Recipe recursiveQuantityRecipe = new();
			recursiveQuantityRecipe.createItem.SetDefaults(ItemID.Wood);
			OrderedRecipeTree recursiveQuantityTree = new(new OrderedRecipeContext(recursiveQuantityRecipe, 0, new SharedCounter(10)), 0);
			recursiveQuantityTree.GetCraftingInformation(null, out CraftResult recursiveQuantityResult);
			Require(recursiveQuantityResult.excessResults.Count == 1 && recursiveQuantityResult.excessResults[0].Stack == 10, "Recursive result quantity was multiplied by its batch count twice.");
			Recipe alternateRoot = new() { RecipeIndex = int.MaxValue - 2 };
			alternateRoot.createItem.SetDefaults(ItemID.WorkBench);
			alternateRoot.requiredItem.Add(new Item(ItemID.StoneBlock, 2));
			Recipe insufficientFirstPath = new() { RecipeIndex = int.MaxValue - 1 };
			insufficientFirstPath.createItem.SetDefaults(ItemID.StoneBlock);
			insufficientFirstPath.requiredItem.Add(new Item(ItemID.Wood, 2));
			Recipe viableSecondPath = new() { RecipeIndex = int.MaxValue };
			viableSecondPath.createItem.SetDefaults(ItemID.StoneBlock);
			viableSecondPath.requiredItem.Add(new Item(ItemID.DirtBlock));
			Recipe competingRoot = new() { RecipeIndex = int.MaxValue - 3 };
			competingRoot.createItem.SetDefaults(ItemID.WorkBench);
			competingRoot.requiredItem.Add(new Item(ItemID.DirtBlock));
			AvailableRecipeObjects alternateInventory = new(new bool[TileLoader.TileCount], new Dictionary<int, int> { [ItemID.Wood] = 2, [ItemID.DirtBlock] = 2 }, null, [], false, static _ => true);
			InventoryCraftabilityGraph alternateGraph = InventoryCraftabilityGraph.Build(alternateInventory, [alternateRoot, competingRoot, insufficientFirstPath, viableSecondPath], 3);
			CraftingSimulation alternateSimulation = new();
			Require(alternateSimulation.TryPlanCraftsWithGraph(new RecursiveRecipe(alternateRoot), 1, alternateInventory, alternateGraph)
				&& alternateSimulation.CraftOperations.Count(operation => operation.recursionDepth == 0 && ReferenceEquals(operation.recipe, alternateRoot)) == 1
				&& !alternateSimulation.CraftOperations.Any(operation => ReferenceEquals(operation.recipe, competingRoot))
				&& alternateSimulation.CraftOperations.Any(operation => ReferenceEquals(operation.recipe, viableSecondPath))
				&& !alternateSimulation.CraftOperations.Any(operation => ReferenceEquals(operation.recipe, insufficientFirstPath)), "Recursive planner changed the requested root recipe or failed to select a viable child path.");
			ulong originalFingerprint = NetHelper.GetRecipeFingerprint(repeatedIngredientRecipe);
			ulong originalRouteFingerprint = NetHelper.GetRecipeRouteFingerprint(repeatedIngredientRecipe);
			repeatedIngredientRecipe.createItem.stack++;
			Require(NetHelper.GetRecipeFingerprint(repeatedIngredientRecipe) != originalFingerprint, "Recipe fingerprint ignored recipe identity changes.");
			Require(NetHelper.GetRecipeRouteFingerprint(repeatedIngredientRecipe) == originalRouteFingerprint, "Recipe route fingerprint changed for a material-only change.");
			repeatedIngredientRecipe.createItem.type = ItemID.StoneBlock;
			Require(NetHelper.GetRecipeRouteFingerprint(repeatedIngredientRecipe) != originalRouteFingerprint, "Recipe route fingerprint ignored output identity changes.");
			Require(NetHelper.GetRecipeTableDigest() == NetHelper.GetRecipeTableDigest(), "Recipe table digest was not stable.");
			Require(Enum.GetValues<CraftRejectionReason>().Length == 9, "Unexpected craft rejection reason set.");
			Require(InboundPacketGuard.ResolvePlayer(7, 3, NetmodeID.Server) == 3, "Server trusted a packet player instead of transport sender.");
			Require(InboundPacketGuard.ResolvePlayer(7, 3, NetmodeID.MultiplayerClient) == 7, "Client discarded the authoritative server player.");
			Guid identity = SecurityPlayer.CreateServerIdentity("Steam:123", "Player");
			Require(identity == SecurityPlayer.CreateServerIdentity("Steam:123", "Player"), "Server security identity was not stable.");
			Require(identity != SecurityPlayer.CreateServerIdentity("Steam:456", "Player"), "Different transports shared a security identity.");
			Require(!InboundPacketGuard.IsValidTransportSender(-1, Main.maxPlayers), "Negative transport sender was accepted.");
			Require(!InboundPacketGuard.IsValidTransportSender(Main.maxPlayers, Main.maxPlayers), "Out-of-range transport sender was accepted.");
			Require(InboundPacketGuard.IsValidTransportSender(0, Main.maxPlayers), "Valid transport sender was rejected before player activation.");
			Require(!InboundPacketGuard.IsValidSender(-1, Main.maxPlayers, active: true), "Negative sender was accepted.");
			Require(!InboundPacketGuard.IsValidSender(Main.maxPlayers, Main.maxPlayers, active: true), "Out-of-range sender was accepted.");
			Require(!InboundPacketGuard.IsValidSender(0, Main.maxPlayers, active: false), "Inactive sender was accepted.");
			Require(InboundPacketGuard.IsValidSender(0, Main.maxPlayers, active: true), "Active sender was rejected.");
			Require(InboundPacketGuard.IsValidCount(InboundPacketGuard.MaxItemEntries, InboundPacketGuard.MaxItemEntries), "Maximum item count was rejected.");
			Require(!InboundPacketGuard.IsValidCount(-1, InboundPacketGuard.MaxItemEntries), "Negative item count was accepted.");
			Require(!InboundPacketGuard.IsValidCount(InboundPacketGuard.MaxItemEntries + 1, InboundPacketGuard.MaxItemEntries), "Oversized item count was accepted.");
			Require(InboundPacketGuard.IsWithinTileRange(new Point(10, 10), new Point16(15, 15), 5, 5), "Inclusive interaction boundary was rejected.");
			Require(!InboundPacketGuard.IsWithinTileRange(new Point(10, 10), new Point16(17, 10), 5, 5), "Out-of-range interaction was accepted.");
			Require(SecuritySystem.IsValidNetworkAssignmentTarget(-1, networkExists: false), "Unassigning a network was rejected.");
			Require(!SecuritySystem.IsValidNetworkAssignmentTarget(-2, networkExists: false), "An invalid negative network ID was accepted.");
			Require(!SecuritySystem.IsValidNetworkAssignmentTarget(10, networkExists: false), "A missing target network was accepted.");
			Require(SecuritySystem.IsValidNetworkAssignmentTarget(10, networkExists: true), "An existing target network was rejected.");
			Require(SecuritySystem.ShouldRetainNetworkAccess(restricted: false, isOwner: false, isOperator: false), "Public network access was revoked.");
			Require(SecuritySystem.ShouldRetainNetworkAccess(restricted: true, isOwner: true, isOperator: false), "Owner network access was revoked.");
			Require(SecuritySystem.ShouldRetainNetworkAccess(restricted: true, isOwner: false, isOperator: true), "Operator network access was revoked.");
			Require(!SecuritySystem.ShouldRetainNetworkAccess(restricted: true, isOwner: false, isOperator: false), "Stale private network access was retained.");
			Require(StorageComponent.ShouldCheckDestroyPermission(fail: false, effectOnly: false, noItem: false), "Direct tile mining bypassed destroy permission checks.");
			Require(!StorageComponent.ShouldCheckDestroyPermission(fail: false, effectOnly: false, noItem: true), "Internal multi-tile cleanup was treated as player mining.");
			Require(NodePool.VerifyParallelIdentityAllocation(), "Parallel recursive node identity allocation produced a duplicate.");
			InventoryCraftabilityProbeFlags combinedPlannerFlags = InventoryCraftabilityProbeFlags.AlternateSameResultRecipe
				| InventoryCraftabilityProbeFlags.CyclicDependencyRegion
				| InventoryCraftabilityProbeFlags.VirtualDependency
				| InventoryCraftabilityProbeFlags.RecipeGroupDependency;
			Require(DemandPlannerAuthority.AreFlagsAuthorized(combinedPlannerFlags, authorizeVirtual: true, authorizeCyclic: true, authorizeAlternate: true), "Fully authorized planner flags were rejected.");
			Require(!DemandPlannerAuthority.AreFlagsAuthorized(combinedPlannerFlags, authorizeVirtual: false, authorizeCyclic: true, authorizeAlternate: true), "Virtual planner risk bypassed its authorization switch.");
			Require(!DemandPlannerAuthority.AreFlagsAuthorized(combinedPlannerFlags, authorizeVirtual: true, authorizeCyclic: false, authorizeAlternate: true), "Cyclic planner risk bypassed its authorization switch.");
			Require(!DemandPlannerAuthority.AreFlagsAuthorized(combinedPlannerFlags, authorizeVirtual: true, authorizeCyclic: true, authorizeAlternate: false), "Alternate-recipe planner risk bypassed its authorization switch.");
			SecurityNetworkModification audit = new(caller.Player, 1, "old-secret", "new-secret", oldRestricted: true, newRestricted: true);
			Require(audit.PreviousPassword is null && audit.CurrentPassword == "[REDACTED]", "Audit credentials were retained in plaintext.");
			Require(!AuditSystem.IsAdministratorSender(-1), "An invalid audit administrator sender was accepted.");
			SecuritySystem.VerifyPasswordPersistencePolicy();

			caller.Reply($"Network policy verification passed: {Enum.GetValues<MessageType>().Length} message types covered.", Color.LightGreen);
		}

		private static void Require(bool condition, string message) {
			if (!condition)
				throw new InvalidOperationException(message);
		}
	}
}
