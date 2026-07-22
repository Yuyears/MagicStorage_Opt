using System;
using MagicStorage.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace MagicStorage.Common.Commands {
	internal sealed class NetworkPolicyVerificationCommand : ModCommand {
		public override CommandType Type => CommandType.Chat;

		public override string Command => "msverifynetpolicy";

		public override string Usage => "/msverifynetpolicy";

		public override string Description => "Runs deterministic inbound packet policy checks.";

		public override void Action(CommandCaller caller, string input, string[] args) {
			foreach (MessageType type in Enum.GetValues<MessageType>()) {
				PacketDirection direction = InboundPacketGuard.GetDirection(type);
				Require(direction != PacketDirection.None, $"{type} has no direction policy.");
				Require(InboundPacketGuard.IsDirectionAllowed(type, NetmodeID.Server) == direction.HasFlag(PacketDirection.ClientToServer), $"{type} server direction mismatch.");
				Require(InboundPacketGuard.IsDirectionAllowed(type, NetmodeID.MultiplayerClient) == direction.HasFlag(PacketDirection.ServerToClient), $"{type} client direction mismatch.");
			}

			Require(!InboundPacketGuard.IsDirectionAllowed((MessageType)byte.MaxValue, NetmodeID.Server), "Unknown packet type was accepted.");
			Require(!InboundPacketGuard.IsValidTransportSender(-1, Main.maxPlayers), "Negative transport sender was accepted.");
			Require(!InboundPacketGuard.IsValidTransportSender(Main.maxPlayers, Main.maxPlayers), "Out-of-range transport sender was accepted.");
			Require(InboundPacketGuard.IsValidTransportSender(0, Main.maxPlayers), "Valid transport sender was rejected before player activation.");
			Require(!InboundPacketGuard.IsValidSender(-1, Main.maxPlayers, active: true), "Negative sender was accepted.");
			Require(!InboundPacketGuard.IsValidSender(Main.maxPlayers, Main.maxPlayers, active: true), "Out-of-range sender was accepted.");
			Require(!InboundPacketGuard.IsValidSender(0, Main.maxPlayers, active: false), "Inactive sender was accepted.");
			Require(InboundPacketGuard.IsValidSender(0, Main.maxPlayers, active: true), "Active sender was rejected.");
			Require(InboundPacketGuard.IsValidCount(InboundPacketGuard.MaxItemEntries, InboundPacketGuard.MaxItemEntries), "Maximum item count was rejected.");
			Require(!InboundPacketGuard.IsValidCount(-1, InboundPacketGuard.MaxItemEntries), "Negative item count was accepted.");
			Require(!InboundPacketGuard.IsValidCount(InboundPacketGuard.MaxItemEntries + 1, InboundPacketGuard.MaxItemEntries), "Oversized item count was accepted.");
			Require(InboundPacketGuard.IsWithinTileRange(new Point(10, 10), new Point16(15, 16), 5, 5), "Inclusive interaction boundary was rejected.");
			Require(!InboundPacketGuard.IsWithinTileRange(new Point(10, 10), new Point16(17, 10), 5, 5), "Out-of-range interaction was accepted.");

			caller.Reply($"Network policy verification passed: {Enum.GetValues<MessageType>().Length} message types covered.", Color.LightGreen);
		}

		private static void Require(bool condition, string message) {
			if (!condition)
				throw new InvalidOperationException(message);
		}
	}
}
