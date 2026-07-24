using MagicStorage.Common.Systems;
using MagicStorage.Common.Systems.Shimmering;
using System.Collections.Generic;
using System.Linq;
using Terraria.ID;

namespace MagicStorage.Common.Threading.Refreshing {
	public interface IShimmerItemReportsProvider {
		ShimmerItemReports ShimmerItemReports { get; }
	}

	public class ShimmerItemReports {
		public readonly ListProvider<ItemReport> reports;

		public ShimmerItemReports(List<ItemReport> staticReportsList) {
			reports = new(staticReportsList);
		}

		public void CollectObjects(int selectedItem) {
			ReplaceReports(selectedItem > ItemID.None
				? MagicCache.ShimmerInfos[selectedItem].GetShimmerReports().OfType<ItemReport>()
				: []);
		}

		internal void ReplaceReports(IEnumerable<ItemReport> source) {
			reports.Clear();
			reports.AddRange(source);
		}

		public void CopyFromStaticCollection() {
			reports.CopyFromStatic();
		}

		public void CopyToStaticCollection() {
			reports.OverwriteStatic();
		}

		public void ClearStaticCollection() {
			reports.ClearStatic();
		}
	}
}
