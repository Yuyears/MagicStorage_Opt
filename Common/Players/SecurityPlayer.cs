using MagicStorage.Common.Systems;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MagicStorage.Common.Players {
	public class SecurityPlayer : ModPlayer {
		/// <summary>
		/// The unique identifier for this player for identifying authors of networks
		/// </summary>
		public Guid UniqueID { get; private set; }

		public static Guid GetID(int plr) => plr < 0 || plr >= Main.maxPlayers ? Guid.Empty : Main.player[plr].GetModPlayer<SecurityPlayer>().UniqueID;

		public static Guid GetLocalID() => Main.LocalPlayer.GetModPlayer<SecurityPlayer>().UniqueID;

		private readonly HashSet<int> _accessibleNetworks = new();
		private readonly Dictionary<int, string> _knownPasswords = new();

		internal const int TEMPORARY_PASSWORD = -1;

		public bool HasJoinedNetwork(int networkID) => _accessibleNetworks.Contains(networkID);

		public void JoinNetwork(int networkID) => _accessibleNetworks.Add(networkID);

		public void RemoveNetworkAccess(int networkID) {
			_accessibleNetworks.Remove(networkID);
			_knownPasswords.Remove(networkID);
		}

		public bool KnowsPassword(int networkID) => _knownPasswords.ContainsKey(networkID);

		public bool TryGetPassword(int networkID, out string password) => _knownPasswords.TryGetValue(networkID, out password);

		internal void RememberPassword(int networkID, string password) => _knownPasswords[networkID] = password;

		internal void ForgetPassword(int networkID) => _knownPasswords.Remove(networkID);

		internal bool RequestingSecurityUI;

		public override void ResetEffects() {
			RequestingSecurityUI = false;
		}

		public override void OnEnterWorld() {
			_accessibleNetworks.Clear();
			_knownPasswords.Clear();
			SecuritySystem.OnLocalClientEnterWorld();
		}

		public override void SaveData(TagCompound tag) {
			if (UniqueID == Guid.Empty)
				UniqueID = Guid.NewGuid();

			tag["id"] = UniqueID.ToByteArray();
		}

		public override void LoadData(TagCompound tag) {
			if (tag.ContainsKey("id"))
				UniqueID = new Guid(tag.GetByteArray("id"));
			else
				UniqueID = Guid.NewGuid();  // Failsafe in case the id was not saved
		}

		// Netcode methods

		public override void SyncPlayer(int toWho, int fromWho, bool newPlayer) {
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				if (newPlayer) {
					ModPacket request = Mod.GetPacket();
					request.Write((byte)MessageType.RequestSecurityPlayerSync);
					request.Send();
				}

				return;
			}

			if (Main.netMode != NetmodeID.Server)
				return;

			string remoteIdentifier = Netplay.Clients[Player.whoAmI].Socket?.GetRemoteAddress()?.GetIdentifier() ?? $"slot:{Player.whoAmI}";
			UniqueID = CreateServerIdentity(remoteIdentifier, Player.name);

			ModPacket packet = Mod.GetPacket();
			packet.Write((byte)MessageType.SecurityPlayerSync);
			packet.Write((byte)Player.whoAmI);
			packet.Write(UniqueID.ToByteArray());
			packet.Send(toWho, fromWho);
		}

		internal void ReceiveSync(BinaryReader reader) {
			byte[] bytes = reader.ReadBytes(16);
			if (bytes.Length == 16)
				UniqueID = new Guid(bytes);
		}

		internal static Guid CreateServerIdentity(string remoteIdentifier, string playerName) {
			byte[] source = Encoding.UTF8.GetBytes($"MagicStorage.SecurityPlayer.v1\0{remoteIdentifier}\0{playerName}");
			return new Guid(SHA256.HashData(source).AsSpan(0, 16));
		}

		public override void CopyClientState(ModPlayer targetCopy) {
			SecurityPlayer mp = (SecurityPlayer)targetCopy;
			mp.UniqueID = UniqueID;
		}

		public override void SendClientChanges(ModPlayer clientPlayer) { }
	}
}
