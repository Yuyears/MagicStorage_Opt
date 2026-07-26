using System.Collections.Generic;
using System.Linq;
using MagicStorage.Components;
using Terraria;

namespace MagicStorage.Common.Threading.Refreshing {
	internal readonly record struct ItemCountSnapshot(int Type, int Prefix, int Stack) {
		public ItemCountSnapshot(Item item) : this(item.type, item.prefix, item.stack) { }
	}

	public interface IStorageItemsProvider {
		StorageItems StorageItems { get; }
	}

	[System.Obsolete("Use IStorageItemsProvider instead")]
	public interface IStorageItemsPovider : IStorageItemsProvider { }

	public class StorageItems {
		public readonly List<Item> allStoredItems = [];
		public int StorageUnitCount { get; private set; }
		public int StoredItemCount { get; private set; }
		public int StoredTypeCount { get; private set; }
		public long StoredQuantity { get; private set; }
		public long TopologyRevision { get; private set; }
		public long ContentRevision { get; private set; }
		public int ChangedUnitCount { get; private set; }
		public int ChangedTypeCount { get; private set; }
		private readonly Dictionary<Terraria.DataStructures.Point16, long> unitRevisions = [];

		public void CollectObjects(RefreshThread thread) {
			List<TEAbstractStorageUnit> units = thread.Performance.Measure(
				RefreshPerformancePhase.Topology,
				() => thread.Heart.GetStorageUnits().ToList());
			TopologyRevision = thread.Heart.StorageTopologyRevision;

			StorageUnitCount = units.Count;
			StoredItemCount = 0;
			StoredTypeCount = 0;
			StoredQuantity = 0;
			allStoredItems.Clear();
			unitRevisions.Clear();
			HashSet<int> identities = [];
			thread.Performance.Measure(RefreshPerformancePhase.Snapshot, () => {
				foreach (TEAbstractStorageUnit unit in units) {
					StorageUnitSnapshot snapshot = unit.GetItemSnapshot();
					unitRevisions[snapshot.Position] = snapshot.Revision;
					foreach (Item item in snapshot.Items) {
						if (item.IsAir)
							continue;

					allStoredItems.Add(item);
					StoredItemCount++;
					StoredQuantity += item.stack;
					identities.Add(item.type);
					}
				}
			});
			StoredTypeCount = identities.Count;
			ContentRevision = thread.Heart.StorageContentRevision;
			thread.Heart.GetStorageChangeSummary(out int changedUnitCount, out int changedTypeCount);
			ChangedUnitCount = changedUnitCount;
			ChangedTypeCount = changedTypeCount;
			thread.CaptureStorageSnapshot(TopologyRevision, ContentRevision, unitRevisions);
		}
	}
}
