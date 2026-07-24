using MagicStorage.Common;
using Microsoft.Xna.Framework;
using SerousCommonLib.API;
using Terraria;

namespace MagicStorage.Edits {
	public class ProjectileExplodeTilesListenerDetours : Edit {
		internal static int ExplodeTilesPlayer = -1;

		public override void LoadEdits() {
			On_Projectile.ExplodeTiles += Projectile_ExplodeTiles;
			On_Projectile.CanExplodeTile += Projectile_CanExplodeTile;
		}

		public override void UnloadEdits() {
			On_Projectile.ExplodeTiles -= Projectile_ExplodeTiles;
			On_Projectile.CanExplodeTile -= Projectile_CanExplodeTile;
		}

		private static bool CanTrackOwner(Projectile projectile)
			=> projectile.friendly && !projectile.hostile && !projectile.npcProj && !projectile.trap && projectile.owner >= 0 && projectile.owner < Main.maxPlayers;

		private static void Projectile_ExplodeTiles(On_Projectile.orig_ExplodeTiles orig, Projectile self, Vector2 compareSpot, int radius, int minI, int maxI, int minJ, int maxJ, bool wallSplode) {
			using (ObjectSwitch.Create(ref ExplodeTilesPlayer, CanTrackOwner(self) ? self.owner : -1))
				orig(self, compareSpot, radius, minI, maxI, minJ, maxJ, wallSplode);
		}

		private static bool Projectile_CanExplodeTile(On_Projectile.orig_CanExplodeTile orig, Projectile self, int x, int y) {
			using (ObjectSwitch.Create(ref ExplodeTilesPlayer, CanTrackOwner(self) ? self.owner : -1))
				return orig(self, x, y);
		}
	}
}
