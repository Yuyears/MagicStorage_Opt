using Microsoft.Xna.Framework;
using Terraria.ModLoader;

namespace MagicStorage.Common.Commands {
	internal sealed class IntegratedVerificationCommand : ModCommand {
		public override string Command => "msverify";

		public override CommandType Type => CommandType.Chat;

		public override string Usage => "/msverify";

		public override string Description => "Runs all deterministic Magic Storage verification commands.";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 0) {
				caller.Reply($"Usage: {Usage}", Color.Red);
				return;
			}

			new SerializationVerificationCommand().Action(caller, "msverifyserialization", []);
			new NetworkPolicyVerificationCommand().Action(caller, "msverifynetpolicy", []);
			caller.Reply("Integrated verification completed; review the results above.", Color.Yellow);
		}
	}
}
