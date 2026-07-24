using MagicStorage.Common.Systems;
using MagicStorage.Components;
using System;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;

namespace MagicStorage.UI {
	public enum PlayerBankInventory : byte {
		PiggyBank,
		Safe,
		DefendersForge,
		VoidVault
	}

	public class UIStorageControlDepositPlayerInventoryButton : UITextPanel<LocalizedText> {
		public Func<Player, Item[]> GetInventory;
		public Action<Player, Item[]> NetReceiveInventoryResult;
		public PlayerBankInventory Inventory;

		internal static Action<Player, Item[]> PendingResultAction;

		public UIStorageControlDepositPlayerInventoryButton(LocalizedText text, float textScale = 1, bool large = false) : base(text, textScale, large) { }

		public override void LeftClick(UIMouseEvent evt) {
			base.LeftClick(evt);

			if (StoragePlayer.LocalPlayer.GetStorageHeart() is not TEStorageHeart heart)
				return;

			if (!StoragePlayer.IsCurrentLocalNetworkAccessible()) {
				SecuritySystem.PrintStorageInaccessible();
				return;
			}

			Item[] inv = GetInventory?.Invoke(Main.LocalPlayer);

			if (inv is null)
				return;  // Nothing to do

			if (Main.netMode == NetmodeID.SinglePlayer) {
				using var access = SecuritySystem.CreateAccessContext();
				TryDepositItems(inv, heart, true, out _);
			} else
				NetHelper.ClientRequestDepositFromBank(StoragePlayer.LocalPlayer.ViewingStorage(), Inventory, NetReceiveInventoryResult);
		}

		internal static void TryDepositItems(Item[] inv, TEStorageHeart heart, bool playSound, out bool changed) {
			changed = false;

			// Try to deposit each item manually so that any leftovers stay in the same slots
			for (int i = 0; i < inv.Length; i++) {
				Item item = inv[i];

				if (item.IsAir || item.favorited)
					continue;

				int stack = item.stack;

				heart.DepositItem(item);

				if (stack != item.stack)
					changed = true;
			}

			if (playSound && changed)
				SoundEngine.PlaySound(SoundID.Grab);
		}
	}
}
