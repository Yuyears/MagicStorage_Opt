using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MagicStorage.Common;
using MagicStorage.Common.Threading;
using MagicStorage.Common.Threading.Refreshing;
using MagicStorage.Components;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MagicStorage.Common.Commands {
	internal sealed class StoragePerformanceCommand : ModCommand {
		private static int running;

		public override CommandType Type => CommandType.Chat | CommandType.Console;
		public override string Command => "msperfstorage";
		public override string Usage => "/msperfstorage [stacks] [units]";
		public override string Description => "Measures deterministic large-inventory storage processing without changing the world.";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length > 2 || !TryRead(args, 0, 3000, 1, 100000, out int stackCount) || !TryRead(args, 1, 20, 1, 1000, out int unitCount)) {
				caller.Reply($"Usage: {Usage} (stacks 1-100000, units 1-1000)", Color.Red);
				return;
			}
			if (Interlocked.CompareExchange(ref running, 1, 0) != 0) {
				caller.Reply("Storage performance benchmark is already running.", Color.OrangeRed);
				return;
			}

			try {
				Stopwatch setup = Stopwatch.StartNew();
				List<List<Item>> units = new(unitCount);
				for (int unit = 0; unit < unitCount; unit++)
					units.Add([]);
				HashSet<(int Type, int Prefix)> identities = [];
				int typeSpan = Math.Max(1, ItemLoader.ItemCount - 1);
				for (int i = 0, candidate = 0; i < stackCount; candidate++) {
					int type = 1 + (candidate % typeSpan);
					Item item = new();
					item.SetDefaults(type);
					if (item.IsAir)
						continue;
					item.prefix = (byte)((i / typeSpan) % byte.MaxValue);
					item.stack = 1 + (i % Math.Max(1, Math.Min(item.maxStack, 999)));
					units[i % unitCount].Add(item);
					identities.Add((item.type, item.prefix));
					i++;
				}
				setup.Stop();
				caller.Reply($"Storage performance benchmark started: stacks={stackCount}, units={unitCount}.", Color.Yellow);

				Task.Factory.StartNew(() => RunBenchmark(caller, units, identities, stackCount, unitCount, setup.Elapsed.TotalMilliseconds), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
			} catch (Exception ex) {
				Interlocked.Exchange(ref running, 0);
				MagicStorageMod.Instance.Logger.Error("Storage performance benchmark setup failed.", ex);
				caller.Reply("Storage performance benchmark setup failed; check the log.", Color.Red);
			}
		}

		private static void RunBenchmark(CommandCaller caller, List<List<Item>> units, HashSet<(int Type, int Prefix)> identities, int stackCount, int unitCount, double setupMs) {
			try {
			Stopwatch total = Stopwatch.StartNew();
			Stopwatch watch = Stopwatch.StartNew();
			long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
			List<List<Item>> topology = [.. units];
			double topologyMs = watch.Elapsed.TotalMilliseconds;

			watch.Restart();
			List<Item> snapshot = new(stackCount);
			foreach (List<Item> unit in topology)
				foreach (Item item in unit)
					snapshot.Add(item.Clone());
			double snapshotMs = watch.Elapsed.TotalMilliseconds;

			watch.Restart();
			ItemAggregateResults aggregate = new(snapshot);
			int progress = 0;
			aggregate.Aggregate(default, uniqueSlotPerItemStack: false, ref progress);
			double aggregateMs = watch.Elapsed.TotalMilliseconds;

			watch.Restart();
			int nonEmptyUnits = units.Count(static unit => unit.Count > 0);
			int totalStacks = units.Sum(static unit => unit.Count);
			double inventoryMs = watch.Elapsed.TotalMilliseconds;

			watch.Restart();
			List<(int Index, List<Item> Unit)> compactionUnits = topology.Select((unit, index) => (index, unit)).ToList();
			int compactionCandidates = TEStorageHeart.EnumerateOrderedCompactionCandidates(compactionUnits, compactionUnits.Where(static unit => unit.Unit.Count > 0).ToList()).Count();
			double compactionScanMs = watch.Elapsed.TotalMilliseconds;
			topology.Clear();
			units.Clear();

			int expectedQuantity = snapshot.Sum(static item => item.stack);
			List<string> countBenchmarks = [];
			double fastestCountMs = double.MaxValue;
			int fastestRequestedWorkers = 1;
			foreach (int requestedWorkers in new[] { 1, 4, 8 }) {
				Dictionary<int, int> counts = [];
				Dictionary<int, Dictionary<int, int>> prefixCounts = [];
				watch.Restart();
				CraftingGUI.BuildItemCounts(snapshot, [], counts, prefixCounts, requestedWorkers, minimumParallelWorkItems: 0);
				double elapsedMs = watch.Elapsed.TotalMilliseconds;
				int countedQuantity = counts.Values.Sum();
				int ignoredStacks = snapshot.Count(static item => item.type <= 0 || item.stack <= 0);
				Require(countedQuantity == expectedQuantity, $"{requestedWorkers}-worker count benchmark changed the total quantity: expected={expectedQuantity}, actual={countedQuantity}, ignoredStacks={ignoredStacks}.");
				int actualWorkers = RefreshParallelism.ResolveWorkerCount(snapshot.Count, requestedWorkers, minimumParallelWorkItems: 0);
				countBenchmarks.Add($"{requestedWorkers}->{actualWorkers}={elapsedMs:F1}ms");
				if (elapsedMs < fastestCountMs) {
					fastestCountMs = elapsedMs;
					fastestRequestedWorkers = requestedWorkers;
				}
			}
			total.Stop();
			long allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
			int aggregateSourceCount = aggregate.SourceCount;
			aggregate = null;

			Require(totalStacks == stackCount && snapshot.Count == stackCount, "Synthetic snapshot lost item stacks.");
			Require(identities.Count > 0 && aggregateSourceCount == stackCount, "Synthetic aggregation did not examine every stack.");
			Require(compactionCandidates == nonEmptyUnits * (nonEmptyUnits - 1) / 2, "Compaction candidate scan changed ordered-pair semantics.");
				string result = $"Storage performance: stacks={stackCount}, identities={identities.Count}, units={unitCount}, nonEmptyUnits={nonEmptyUnits}, setup={setupMs:F1}ms " +
					$"topology={topologyMs:F1}ms snapshot={snapshotMs:F1}ms aggregate={aggregateMs:F1}ms inventory={inventoryMs:F1}ms compactionCandidates={compactionCandidates} compactionScan={compactionScanMs:F1}ms allocated={allocatedBytes / 1024d / 1024d:F1}MiB counts=[{string.Join(", ", countBenchmarks)}] countWinner={fastestRequestedWorkers} total={total.Elapsed.TotalMilliseconds:F1}ms";
				Main.QueueMainThreadAction(() => {
					MagicStorageMod.Instance.Logger.Info("Storage performance baseline: " + result[21..]);
					caller.Reply(result, Color.LightGreen);
				});
			} catch (Exception ex) {
				Main.QueueMainThreadAction(() => {
					MagicStorageMod.Instance.Logger.Error("Storage performance benchmark failed.", ex);
					caller.Reply("Storage performance benchmark failed; check the log.", Color.Red);
				});
			} finally {
				Interlocked.Exchange(ref running, 0);
			}
		}

		private static bool TryRead(string[] args, int index, int fallback, int min, int max, out int value) {
			value = fallback;
			if (index >= args.Length)
				return true;
			if (!int.TryParse(args[index], out value) || value < min || value > max)
				return false;
			return true;
		}

		private static void Require(bool condition, string message) {
			if (!condition)
				throw new InvalidOperationException(message);
		}
	}
}
