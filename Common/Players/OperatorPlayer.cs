using MagicStorage.Common.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MagicStorage.Common.Players {
	public class OperatorPlayer : ModPlayer {
		public bool hasOp;

		internal bool manualOp;

		public bool IsAdministrator => hasOp && manualOp;

		public override void OnEnterWorld() {
			Netcode.RequestingOperatorKey = false;
		}

		public override void PreUpdate() {
			if (Main.netMode == NetmodeID.Server && MagicStorageServerConfig.GiveLocalHostAdminOnJoin) {
				int whoAmI = Player.whoAmI;
				if (!hasOp && Main.countsAsHostForGameplay[whoAmI])
					NetHelper.ServerSetPlayerOperator(whoAmI, hasOp: true, manualOp: true);
			}
		}

		public override void SyncPlayer(int toWho, int fromWho, bool newPlayer) {
			if (Main.netMode == NetmodeID.Server)
				NetHelper.ServerPreparePlayerHasOperatorPacket(Player.whoAmI, this).Send(toWho, fromWho);
		}
	}
}
