namespace MagicStorage.Common.Systems.RecurrentRecipes {
	internal enum DemandPlannerAuthorityClass {
		Unsupported,
		Direct,
		RecipeGroup,
		VirtualDependency,
		CyclicBounded,
		RootAlternate
	}

	internal static class DemandPlannerAuthority {
		private const bool AuthorizeCyclicBoundedForUi = true;
		private const bool AuthorizeRootAlternateForUi = true;

		public static bool IsVirtualDependencyUiAuthorized => true;

		public static bool IsCyclicBoundedUiAuthorized => AuthorizeCyclicBoundedForUi;

		public static bool IsRootAlternateUiAuthorized => AuthorizeRootAlternateForUi;

		public static DemandPlannerAuthorityClass Classify(InventoryCraftabilityRecipeProbe probe) {
			if (!probe.HasCandidate)
				return DemandPlannerAuthorityClass.Unsupported;

			if ((probe.Flags & InventoryCraftabilityProbeFlags.AlternateSameResultRecipe) != 0)
				return DemandPlannerAuthorityClass.RootAlternate;

			if ((probe.Flags & InventoryCraftabilityProbeFlags.CyclicDependencyRegion) != 0)
				return DemandPlannerAuthorityClass.CyclicBounded;

			if ((probe.Flags & InventoryCraftabilityProbeFlags.VirtualDependency) != 0)
				return DemandPlannerAuthorityClass.VirtualDependency;

			if ((probe.Flags & InventoryCraftabilityProbeFlags.RecipeGroupDependency) != 0)
				return DemandPlannerAuthorityClass.RecipeGroup;

			return DemandPlannerAuthorityClass.Direct;
		}

		public static bool IsUiAuthorized(InventoryCraftabilityRecipeProbe probe) {
			return probe.HasCandidate && AreFlagsAuthorized(
				probe.Flags,
				IsVirtualDependencyUiAuthorized,
				AuthorizeCyclicBoundedForUi,
				AuthorizeRootAlternateForUi);
		}

		public static bool IsDiagnosticSupported(InventoryCraftabilityRecipeProbe probe)
			=> probe.HasCandidate && AreFlagsAuthorized(probe.Flags, authorizeVirtual: true, authorizeCyclic: true, authorizeAlternate: true);

		internal static bool AreFlagsAuthorized(InventoryCraftabilityProbeFlags flags, bool authorizeVirtual, bool authorizeCyclic, bool authorizeAlternate) {
			if (!authorizeAlternate && (flags & InventoryCraftabilityProbeFlags.AlternateSameResultRecipe) != 0)
				return false;
			if (!authorizeCyclic && (flags & InventoryCraftabilityProbeFlags.CyclicDependencyRegion) != 0)
				return false;
			if (!authorizeVirtual && (flags & InventoryCraftabilityProbeFlags.VirtualDependency) != 0)
				return false;

			return true;
		}

		public static bool IsChildCandidateSupported(InventoryCraftabilityRecipeProbe probe, bool allowAlternateSameResult) {
			if (!probe.HasCandidate)
				return false;

			return allowAlternateSameResult
				|| (probe.Flags & InventoryCraftabilityProbeFlags.AlternateSameResultRecipe) == 0;
		}

		public static bool IsTreePreviewSupported(InventoryCraftabilityRecipeProbe probe)
			=> Classify(probe) == DemandPlannerAuthorityClass.Direct;
	}
}
