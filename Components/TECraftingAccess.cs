using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using System.Collections.Concurrent;
using Terraria.DataStructures;
using MagicStorage.Common.Systems;
using MagicStorage.Common.IO;
using System.Threading;
using System.Diagnostics;

namespace MagicStorage.Components
{
	public class TECraftingAccess : TEStorageAccess
	{
		public enum Operation : byte
		{
			Withdraw,
			WithdrawToInventory,
			Deposit,
			DepositCommit,
		}

		private static Item pendingDeposit;
		private static Point16 pendingDepositPosition;
		private static long nextOperationId, pendingDepositOperationId;
		private static long pendingDepositStartedAt;
		private static bool pendingDepositCommitSent;

		private class NetOperation
		{
			public NetOperation(Operation _type, long _operationId, int _slot, int _client, Item _item = null)
			{
				type = _type;
				operationId = _operationId;
				slot = _slot;
				client = _client;
				item = _item;
			}

			public Operation type { get; }
			public long operationId { get; }
			public int slot { get; }
			public int client { get; }
			public Item item { get; }
		}
		ConcurrentQueue<NetOperation> clientOpQ = new ConcurrentQueue<NetOperation>();

		public const int Rows = 3;
		public const int Columns = 15;
		public const int ItemsTotal = Rows * Columns;
		internal static int DepositInventorySlot => PlayerItemSlotID.InventoryMouseItem;

		internal static bool IsValidDepositSourceSlot(int slot) => slot == DepositInventorySlot;

		internal static bool IsValidStationItem(Item item) => item is { IsAir: false };

		//public Item[] stations = new Item[ItemsTotal];
		public List<Item> stations = new List<Item>();

		public TECraftingAccess()
		{
		}

		public override void Update()
		{
			base.Update();

			if (Main.netMode == NetmodeID.Server)
			{
				processClientOperations();
			}
		}

		private void processClientOperations()
		{
			int opCount = clientOpQ.Count;
			if (opCount > 0)
			{
				for (int i = 0; i < opCount; ++i)
				{
					NetOperation op;
					if (clientOpQ.TryDequeue(out op))
					{
						if (op.type == Operation.Withdraw || op.type == Operation.WithdrawToInventory)
						{
							Item item = WithdrawStation(op.slot);
							SendServerResult(Position, op.type, op.operationId, op.client, !item.IsAir, item);
							if (item.IsAir)
								continue;
						}
						else
						{
							Player player = Main.player[op.client];
							if (op.type != Operation.DepositCommit || !IsValidDepositSourceSlot(op.slot) || player?.active != true || !IsValidStationItem(op.item))
								continue;

							Item item = op.item;
							int oldType = item.type;
							int oldStationCount = stations.Count;
							DepositStation(item);
							player.inventory[op.slot] = item.Clone();
							NetMessage.SendData(MessageID.SyncEquipment, op.client, -1, null, op.client, op.slot);

							SendServerResult(Position, op.type, op.operationId, op.client, accepted: stations.Count > oldStationCount, item, oldType);
						}
						NetHelper.SendTEUpdate(ID, Position);
					}
				}

				Point16 pos = Position;
				StorageAccess modTile = TileLoader.GetTile(Main.tile[pos.X, pos.Y].TileType) as StorageAccess;
				TEStorageHeart heart = modTile?.GetHeart(pos.X, pos.Y);
				if (heart is not null)
					NetHelper.SendRefreshNetworkItems(heart.Position, ignoreSpecificRefreshes: true);
			}
		}

		public void QClientOperation(Operation op, long operationId, int slot, int client)
		{
			NetOperation netOp;
			if (op == Operation.Withdraw || op == Operation.WithdrawToInventory)
			{
				if (slot < 0 || slot >= stations.Count) {
					SendServerResult(Position, op, operationId, client, accepted: false, new Item());
					return;
				}

				netOp = new NetOperation(op, operationId, slot, client);

			//	NetHelper.PrintClientRequest(client, "Item Withdraw", Position);
			}
			else if (op == Operation.Deposit)
			{
				Player player = Main.player[client];
				Item item = IsValidDepositSourceSlot(slot) && player?.active == true ? player.inventory[slot] : null;
				bool accepted = Main.netMode == NetmodeID.Server && CanDepositStation(item);
				SendServerResult(Position, op, operationId, client, accepted, new Item());
				return;
			}
			else if (op == Operation.DepositCommit)
			{
				Player player = Main.player[client];
				if (Main.netMode != NetmodeID.Server || !IsValidDepositSourceSlot(slot) || player?.active != true) {
					SendServerResult(Position, op, operationId, client, accepted: false, new Item());
					return;
				}

				Item item = player.inventory[slot];
				if (!CanDepositStation(item)) {
					SendServerResult(Position, op, operationId, client, accepted: false, item.Clone());
					return;
				}

				netOp = new NetOperation(op, operationId, slot, client, item.Clone());
				item.TurnToAir();

			//	NetHelper.PrintClientRequest(client, "Item Deposit", Position);
			} else
				return;

			if (netOp is not null && Main.netMode == NetmodeID.Server)
				clientOpQ.Enqueue(netOp);
		}

		internal static void SendServerResult(Point16 position, Operation op, long operationId, int client, bool accepted, Item item, int oldType = ItemID.None)
		{
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ServerStationOperationResult);
			packet.Write((byte)op);
			packet.Write(operationId);
			packet.Write(accepted);
			packet.Write(position);
			ItemIO.Send(item ?? new Item(), packet, true, true);
			if (op == Operation.DepositCommit)
				packet.Write((ushort)oldType);
			packet.Send(client);
		}

		private ModPacket PrepareClientRequest(Operation op)
		{
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientStationOperation);
			packet.Write(Position);
			packet.Write((byte)op);
			return packet;
		}

		public override bool ValidTile(in Tile tile) => tile.TileType == ModContent.TileType<CraftingAccess>() && tile.TileFrameX == 0 && tile.TileFrameY == 0;

		private Item DepositStation(Item item)
		{
			if (!CanDepositStation(item))
				return item;

			Item nItem = item.Clone();
			nItem.stack = 1;
			nItem.favorited = false;
			stations.Add(nItem);
			item.stack--;
			if (item.stack <= 0)
				item.SetDefaults();

			if (Main.netMode != NetmodeID.Server)
				UpdateRecipesFromStationAction(nItem);

			return item;
		}

		private bool CanDepositStation(Item item) {
			NormalizeStations();
			return IsValidStationItem(item) && stations.Count < ItemsTotal && !stations.Any(station => station.type == item.type);
		}

		public Item TryDepositStation(Item item)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				if (pendingDeposit is not null) {
					if (!pendingDepositCommitSent && TEStorageHeart.IsNetworkOperationTimedOut(pendingDepositStartedAt, Stopwatch.GetTimestamp()))
						ClearPendingDeposit();
					else
						return item;
				}

				int slot = DepositInventorySlot;
				Main.LocalPlayer.inventory[slot] = item.Clone();
				NetMessage.SendData(MessageID.SyncEquipment, number: Main.myPlayer, number2: slot);
				pendingDeposit = item.Clone();
				pendingDepositPosition = Position;
				pendingDepositOperationId = Interlocked.Increment(ref nextOperationId);
				pendingDepositStartedAt = Stopwatch.GetTimestamp();
				pendingDepositCommitSent = false;

				ModPacket packet = PrepareClientRequest(Operation.Deposit);
				packet.Write(pendingDepositOperationId);
				packet.Write((byte)slot);
				packet.Send();
			}
			else
			{
				DepositStation(item);
			}

			return item;
		}

		private Item WithdrawStation(int slot)
		{
			NormalizeStations();
			if (slot >= stations.Count)
				return new Item();

			var item = stations[slot];
			stations.RemoveAt(slot);

			if (Main.netMode != NetmodeID.Server)
				UpdateRecipesFromStationAction(item);

			return item;
		}

		internal static void ReceiveDepositPreparation(Point16 position, long operationId, bool accepted) {
			if (pendingDeposit is null || pendingDepositPosition != position || pendingDepositOperationId != operationId)
				return;

			if (!accepted || !Utility.AreStrictlyEqual(Main.mouseItem, pendingDeposit, checkStack: true)) {
				CancelPendingDeposit();
				return;
			}

			Main.mouseItem.TurnToAir();
			pendingDepositCommitSent = true;
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientStationOperation);
			packet.Write(position);
			packet.Write((byte)Operation.DepositCommit);
			packet.Write(operationId);
			packet.Write((byte)DepositInventorySlot);
			packet.Send();
		}

		internal static void ReceiveDepositCommit(Point16 position, long operationId, bool accepted, Item item) {
			if (pendingDeposit is null || pendingDepositPosition != position || pendingDepositOperationId != operationId)
				return;

			if (!accepted && item.IsAir)
				item = pendingDeposit;

			Main.LocalPlayer.inventory[DepositInventorySlot] = item.Clone();
			Main.mouseItem = item;
			ClearPendingDeposit();
		}

		private static void CancelPendingDeposit() {
			Main.LocalPlayer.inventory[DepositInventorySlot].TurnToAir();
			NetMessage.SendData(MessageID.SyncEquipment, number: Main.myPlayer, number2: DepositInventorySlot);
			ClearPendingDeposit();
		}

		internal static void ClearPendingDeposit() {
			pendingDeposit = null;
			pendingDepositPosition = default;
			pendingDepositOperationId = 0;
			pendingDepositStartedAt = 0;
			pendingDepositCommitSent = false;
		}

		private void NormalizeStations() => stations.RemoveAll(static item => !IsValidStationItem(item));

		internal static void UpdateRecipesFromStationAction(Item station) {
			// Ensure that refreshing can't affect this method
			CraftingGUI._blockForStationUpdate = true;
			while (CraftingGUI._executingInGuiEnvironment > 0)
				Thread.Yield();

			/*
			CraftingGUI.PlayerZoneCache.Cache();

			Player player = Main.LocalPlayer;

			for (int i = 0; i < player.adjTile.Length; i++)
				player.adjTile[i] = false;

			player.adjWater = false;
			player.adjLava = false;
			player.adjHoney = false;
			player.adjShimmer = false;
			*/

			UpdateRecipes(station);

		//	CraftingGUI.PlayerZoneCache.FreeCache(destroy: true);

			CraftingGUI._blockForStationUpdate = false;
		}

		private static void UpdateRecipes(Item station) {
			var information = CraftingGUI.ReadCraftingEnvironment();
			var oldInformation = information.Clone();

			Utility.AddCraftingZones(station, ref information);

			MagicUI.RequestMainZoneThread();
			CraftingGUI.SetNextDefaultRecipeCollectionToRefreshFromTile(GetUpdatedAdjTile(oldInformation.adjTiles, information.adjTiles));

			if (oldInformation.water != information.water)
				CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingWater);
			if (oldInformation.lava != information.lava)
				CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingLava);
			if (oldInformation.honey != information.honey)
				CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingHoney);
			if (oldInformation.shimmer != information.shimmer)
				CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingShimmer);
			if (oldInformation.snow != information.snow)
				CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingSnow);
			if (oldInformation.graveyard != information.graveyard)
				CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingEctoMist);

			if (CraftingGUI.GetHeart() is TEStorageHeart heart) {
				foreach (EnvironmentModule module in heart.GetModules()) {
					Recipe[] recipes = module.GetRecipesToRefresh(station)?.ToArray();

					if (recipes is not null)
						CraftingGUI.SetNextDefaultRecipeCollectionToRefresh(recipes);
				}
			}

			CraftingGUI.WriteCraftingEnvironment(information);
		}

		private static IEnumerable<int> GetUpdatedAdjTile(bool[] oldAdjTile, bool[] adjTile) {
			for (int i = 0; i < adjTile.Length; i++) {
				if (oldAdjTile[i] != adjTile[i])
					yield return i;
			}
		}

		public Item TryWithdrawStation(int slot, bool toInventory = false)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				ModPacket packet = PrepareClientRequest(toInventory ? Operation.WithdrawToInventory : Operation.Withdraw);
				packet.Write(Interlocked.Increment(ref nextOperationId));
				packet.Write((byte) slot);
				packet.Send();

				return new Item();
			}

			var item = WithdrawStation(slot);
			StoragePlayer.GetItem(new EntitySource_TileEntity(this), item, !toInventory);

			return item;
		}

		public override void SaveData(TagCompound tag)
		{
			base.SaveData(tag);

			tag["Stations"] = stations.Select(Utility.SaveItem).Where(t => t.Count > 0).ToList();
		}

		public override void LoadData(TagCompound tag)
		{
			base.LoadData(tag);

			IList<TagCompound> listStations = tag.GetList<TagCompound>("Stations");
			if (listStations is not null && listStations.Count > 0)
			{
				foreach (TagCompound stationTag in listStations)
				{
					Item item = Utility.SafelyLoadItem(stationTag);
					if (!item.IsAir)
					{
						stations.Add(item);
					}
				}
			}
		}

		public override void NetSend(BinaryWriter writer)
		{
			base.NetSend(writer);
			NetCompression.SendItems(stations, writer, listCountBitSizeOverride: NetCompression.GetBitSize(Columns * Rows));
		}

		public override void NetReceive(BinaryReader reader)
		{
			base.NetReceive(reader);
			stations = NetCompression.ReceiveItems(reader, listCountBitSizeOverride: NetCompression.GetBitSize(Columns * Rows));
			NormalizeStations();
		}
	}
}
