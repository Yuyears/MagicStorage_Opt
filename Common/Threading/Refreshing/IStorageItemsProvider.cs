using System.Collections.Generic;
using System.Linq;
using Terraria;

namespace MagicStorage.Common.Threading.Refreshing {
	public interface IStorageItemsProvider {
		StorageItems StorageItems { get; }
	}

	[System.Obsolete("Use IStorageItemsProvider instead")]
	public interface IStorageItemsPovider : IStorageItemsProvider { }

	public class StorageItems {
		public readonly List<Item> allStoredItems = [];

		public void CollectObjects(RefreshThread thread) {
			allStoredItems.Clear();
			allStoredItems.AddRange(thread.Heart.GetStoredItems().Select(static item => item.Clone()));
		}
	}
}
