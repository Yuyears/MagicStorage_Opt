using MagicStorage.Common;
using MagicStorage.Common.Systems;
using MagicStorage.Common.Threading;
using MagicStorage.Common.Threading.Refreshing;
using MagicStorage.Sorting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace MagicStorage {
	partial class CraftingGUI {
		internal static readonly List<Item> items = new();
		internal static readonly List<List<Item>> itemGroups = new();
		internal static readonly List<Item> unfilteredItems = new();

		internal static readonly Dictionary<int, int> itemCounts = new();
		internal static readonly Dictionary<int, Dictionary<int, int>> itemCountsByPrefix = new();
		internal static readonly StaticValue<int> itemCountsHash = new();

		internal static readonly HashSet<int> isItemInfinite = [];
		internal static bool allItemsAreInfinite;

		// Only used by DoWithdrawResult to check items from modules
		internal static readonly List<Item> sourceItemsFromModules = new();

		// Caches for StoredIngredientsRefreshThread
		internal static readonly ConditionalWeakTable<Item, object> wasModuleItem = [];
		internal static readonly ConditionalWeakTable<Item, object> moduleItemWasFromInventory = [];

		internal static bool hasCompleteData;
		
		[Obsolete("Use MagicUI.RefreshItems() instead", error: true)]
		public static void RefreshItems() => MagicUI.RefreshItems();

		internal static void ResetRefreshCache() => ClearRecipeRefreshOptimizationState();
		
		internal static void RefreshItems_Inner() {
			// Always reset the cached values
			ResetRecentRecipeCache();

			lastKnownRecursionErrorForStoredItems = null;

			NetHelper.Report(true, "CraftingGUI: RefreshItems invoked");

			if (recipesToRefreshByIndex is { Count: > 0 })
				NetHelper.Report(false, $"Refreshing {recipesToRefreshByIndex.Count} recipes...");

			CreateFullRefreshThread(caller: "CraftingGUI.RefreshItems()").Start();

			ResetRefreshCache();
		}

		private static void SortAndFilter(CraftingRefreshThread thread) {
			LoadItemsAndSetDictionaryInfo(thread);
			PrepareInventoryCraftabilityGraph(thread, buildIfCacheMiss: true);
			RefreshStorageItems(thread);
			RefreshRecipes(thread);
		}

		// Moved to internal method for use by DecraftingGUI
		internal static void LoadItemsAndSetDictionaryInfo<T>(T thread)
			where T : RefreshThread, IStorageItemsProvider, IProcessedStorageItemsProvider
		{
			LoadItemsAndSetDictionaryInfo(thread, thread.StorageItems);
		}

		internal static void LoadItemsAndSetDictionaryInfo<T>(T thread, StorageItems storage)
			where T : RefreshThread, IProcessedStorageItemsProvider
		{
			var processed = thread.ProcessedStorageItems;
			SetUnfilteredItems(processed, storage);

			// Organize the items from the storage system
			thread.workingItemList = storage.allStoredItems;
			thread.workingCounter = storage.allStoredItems.Count;
			thread.workingFlag = false;

			var storedItems = ItemSorter.SortAndFilterItems(thread, 0);

			processed.resultItems.Clear();
			processed.resultItemGroups.Clear();

			processed.resultItems.AddRange(storedItems);

			thread.aggregateResults.MoveResultGroupsTo(processed.resultItemGroups.Value);

			int numModuleItems = 0;
			processed.resultItemsFromModules.Clear();

			if (processed.allModuleItems is { Count: > 0 }) {
				// Organize the items from the modules
				thread.workingItemList = processed.allModuleItems;
				thread.workingCounter = processed.allModuleItems.Count;
				thread.workingFlag = true;  // uniqueSlotPerItemStack

				var moduleItems = ItemSorter.SortAndFilterItems(thread, 0, listClassification: "Module");

				processed.resultItems.AddRange(moduleItems);

				processed.resultItemsFromModules.AddRange(processed.allModuleItems);

				numModuleItems = moduleItems.Count;
			}

			SetCountsDictionaries(thread, storage.allStoredItems, processed.moduleCountSnapshot ?? []);

			thread.workingItemList = null;
			thread.workingCounter = 0;
			thread.workingFlag = false;

			NetHelper.Report(false, "Total items: " + processed.resultItems.Count);
			NetHelper.Report(false, "Items from modules: " + numModuleItems);
		}

		internal static void LoadInventoryCountsOnly<T>(T thread, StorageItems storage)
			where T : RefreshThread, IProcessedStorageItemsProvider
		{
			SetUnfilteredItems(thread.ProcessedStorageItems, storage);
			SetCountsDictionaries(thread, storage.allStoredItems, thread.ProcessedStorageItems.moduleCountSnapshot ?? []);
		}

		private static void SetUnfilteredItems(ProcessedStorageItems processed, StorageItems storage)
			=> CopyUnfilteredItems(processed.unfilteredItems.Value, storage.allStoredItems, processed.allModuleItems ?? []);

		internal static void CopyUnfilteredItems(List<Item> destination, IEnumerable<Item> storageItems, IEnumerable<Item> moduleItems) {
			destination.Clear();
			destination.AddRange(storageItems);
			destination.AddRange(moduleItems);
		}

		internal static void SetCountsDictionaries<T>(T thread)
			where T : RefreshThread, IProcessedStorageItemsProvider
		{
			SetCountsDictionaries(thread, thread.ProcessedStorageItems.resultItems.Value);
		}

		internal static void SetCountsDictionaries<T>(T thread, IEnumerable<Item> sourceItems)
			where T : RefreshThread, IProcessedStorageItemsProvider
		{
			long countingStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
			var processed = thread.ProcessedStorageItems;

			var itemCounts = processed.itemCounts;
			var itemCountsByPrefix = processed.itemCountsByPrefix;

			itemCounts.Clear();
			itemCountsByPrefix.Clear();
			processed.itemCountsHash.Value = 0;

			int totalItems = sourceItems.TryGetNonEnumeratedCount(out int count) ? count : 0;
			thread.InitTaskSchedule(totalItems, "Counting Items");

			foreach (Item item in sourceItems.NotifyStepsTo(thread).WatchForCancellation(thread, 16)) {
				if (item is not { IsAir: false })
					continue;

				if (itemCounts.TryGetValue(item.type, out int quantity))
					itemCounts[item.type] = new ClampedArithmetic(quantity) + item.stack;
				else
					itemCounts[item.type] = item.stack;

				if (itemCountsByPrefix.TryGetValue(item.type, out var prefixCounts)) {
					if (prefixCounts.TryGetValue(item.prefix, out quantity))
						prefixCounts[item.prefix] = new ClampedArithmetic(quantity) + item.stack;
					else
						prefixCounts[item.prefix] = item.stack;
				} else
					itemCountsByPrefix[item.type] = new Dictionary<int, int>() { [item.prefix] = item.stack };
			}

			processed.itemCountsHash.Value = GetCountsHash(itemCounts.Value);
			thread.Performance.AddElapsed(RefreshPerformancePhase.Counting, countingStartedAt);
		}

		private static void SetCountsDictionaries<T>(T thread, IReadOnlyList<Item> storageItems, IReadOnlyList<ItemCountSnapshot> moduleItems)
			where T : RefreshThread, IProcessedStorageItemsProvider
		{
			long countingStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
			var processed = thread.ProcessedStorageItems;
			thread.InitTaskSchedule(storageItems.Count + moduleItems.Count, "Counting Items");

			BuildItemCounts(
				storageItems,
				moduleItems,
				processed.itemCounts.Value,
				processed.itemCountsByPrefix.Value,
				RefreshParallelism.DefaultWorkerCount,
				thread.cancellationToken,
				thread.Complete);

			processed.itemCountsHash.Value = GetCountsHash(processed.itemCounts.Value);
			thread.Performance.AddElapsed(RefreshPerformancePhase.Counting, countingStartedAt);
		}

		internal static void BuildItemCounts(
			IReadOnlyList<Item> storageItems,
			IReadOnlyList<ItemCountSnapshot> moduleItems,
			Dictionary<int, int> itemCounts,
			Dictionary<int, Dictionary<int, int>> itemCountsByPrefix,
			int requestedWorkers,
			CancellationToken cancellationToken = default,
			Action<int> reportProgress = null,
			int minimumParallelWorkItems = RefreshParallelism.MinimumParallelWorkItems)
		{
			ArgumentNullException.ThrowIfNull(storageItems);
			ArgumentNullException.ThrowIfNull(moduleItems);
			ArgumentNullException.ThrowIfNull(itemCounts);
			ArgumentNullException.ThrowIfNull(itemCountsByPrefix);

			itemCounts.Clear();
			itemCountsByPrefix.Clear();
			int total = storageItems.Count + moduleItems.Count;
			int workerCount = RefreshParallelism.ResolveWorkerCount(total, requestedWorkers, minimumParallelWorkItems);
			Dictionary<int, int>[] localCounts = new Dictionary<int, int>[workerCount];
			Dictionary<int, Dictionary<int, int>>[] localPrefixCounts = new Dictionary<int, Dictionary<int, int>>[workerCount];

			void CountPartition(int worker) {
				Dictionary<int, int> counts = localCounts[worker] = [];
				Dictionary<int, Dictionary<int, int>> prefixCounts = localPrefixCounts[worker] = [];
				int start = total * worker / workerCount;
				int end = total * (worker + 1) / workerCount;

				for (int index = start; index < end; index++) {
					if ((index & 63) == 0)
						cancellationToken.ThrowIfCancellationRequested();

					ItemCountSnapshot item = index < storageItems.Count ? new ItemCountSnapshot(storageItems[index]) : moduleItems[index - storageItems.Count];
					if (item.Type <= 0 || item.Stack <= 0)
						continue;

					counts[item.Type] = new ClampedArithmetic(counts.GetValueOrDefault(item.Type)) + item.Stack;
					if (!prefixCounts.TryGetValue(item.Type, out Dictionary<int, int> byPrefix))
						prefixCounts[item.Type] = byPrefix = [];
					byPrefix[item.Prefix] = new ClampedArithmetic(byPrefix.GetValueOrDefault(item.Prefix)) + item.Stack;
				}

				reportProgress?.Invoke(end - start);
			}

			if (workerCount == 1)
				CountPartition(0);
			else
				Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = cancellationToken }, CountPartition);

			for (int worker = 0; worker < workerCount; worker++) {
				foreach ((int type, int count) in localCounts[worker])
					itemCounts[type] = new ClampedArithmetic(itemCounts.GetValueOrDefault(type)) + count;

				foreach ((int type, Dictionary<int, int> localByPrefix) in localPrefixCounts[worker]) {
					if (!itemCountsByPrefix.TryGetValue(type, out Dictionary<int, int> byPrefix))
						itemCountsByPrefix[type] = byPrefix = [];
					foreach ((int prefix, int count) in localByPrefix)
						byPrefix[prefix] = new ClampedArithmetic(byPrefix.GetValueOrDefault(prefix)) + count;
				}
			}
		}
	}
}
