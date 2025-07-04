#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Commands
{
	[TraitLocation(SystemActors.World)]
	[Desc("Enables developer cheats via the chatbox. Attach this to the world actor.")]
	public class DevCommandsInfo : TraitInfo<DevCommands> { }

	public class DevCommands : IChatCommand, IWorldLoaded
	{
		[FluentReference]
		const string CheatsDisabled = "notification-cheats-disabled";

		[FluentReference]
		const string InvalidCashAmount = "notification-invalid-cash-amount";

		[FluentReference]
		const string ToggleVisiblityDescription = "description-toggle-visibility";

		[FluentReference]
		const string ToggleVisiblityAllDescription = "description-toggle-visibility-all";

		[FluentReference]
		const string GiveCashDescription = "description-give-cash";

		[FluentReference]
		const string GiveCashAllDescription = "description-give-cash-all";

		[FluentReference]
		const string InstantBuildingDescription = "description-instant-building";

		[FluentReference]
		const string InstantBuildingAllDescription = "description-instant-building-all";

		[FluentReference]
		const string BuildAnywhereDescription = "description-build-anywhere";

		[FluentReference]
		const string BuildAnywhereAllDescription = "description-build-anywhere-all";

		[FluentReference]
		const string UnlimitedPowerDescription = "description-unlimited-power";

		[FluentReference]
		const string UnlimitedPowerAllDescription = "description-unlimited-power-all";

		[FluentReference]
		const string EnableTechDescription = "description-enable-tech";

		[FluentReference]
		const string EnableTechAllDescription = "description-enable-tech-all";

		[FluentReference]
		const string FastChargeDescription = "description-fast-charge";

		[FluentReference]
		const string FastChargeAllDescription = "description-fast-charge-all";

		[FluentReference]
		const string DevCheatAllDescription = "description-dev-cheat-all";

		[FluentReference]
		const string DevCheatAllForAllDescription = "description-dev-cheat-all-for-all";

		[FluentReference]
		const string DevCrashDescription = "description-dev-crash";

		[FluentReference]
		const string LevelUpActorDescription = "description-levelup-actor";

		[FluentReference]
		const string PlayerExperienceDescription = "description-player-experience";

		[FluentReference]
		const string PowerOutageDescription = "description-power-outage";

		[FluentReference]
		const string KillSelectedActorsDescription = "description-kill-selected-actors";

		[FluentReference]
		const string DisposeSelectedActorsDescription = "description-dispose-selected-actors";

		[FluentReference]
		const string ProduceFromSelectedActorsDescription = "description-produce-from-selected-actors";

		[FluentReference]
		const string ClearResourcesDescription = "description-clear-resources";

		readonly IDictionary<string, (string Description, Action<string, World> Handler)> commandHandlers = new Dictionary<string, (string, Action<string, World>)>
		{
			{ "visibility", (ToggleVisiblityDescription, Visibility) },
			{ "visibility-all", (ToggleVisiblityAllDescription, VisibilityAll) },
			{ "give-cash", (GiveCashDescription, GiveCash) },
			{ "give-cash-all", (GiveCashAllDescription, GiveCashAll) },
			{ "instant-build", (InstantBuildingDescription, InstantBuild) },
			{ "instant-build-all", (InstantBuildingAllDescription, InstantBuildAll) },
			{ "build-anywhere", (BuildAnywhereDescription, BuildAnywhere) },
			{ "build-anywhere-all", (BuildAnywhereAllDescription, BuildAnywhereAll) },
			{ "unlimited-power", (UnlimitedPowerDescription, UnlimitedPower) },
			{ "unlimited-power-all", (UnlimitedPowerAllDescription, UnlimitedPowerAll) },
			{ "enable-tech", (EnableTechDescription, EnableTech) },
			{ "enable-tech-all", (EnableTechAllDescription, EnableTechAll) },
			{ "fast-charge", (FastChargeDescription, FastCharge) },
			{ "fast-charge-all", (FastChargeAllDescription, FastChargeAll) },
			{ "all", (DevCheatAllDescription, All) },
			{ "all-for-all", (DevCheatAllForAllDescription, AllForAll) },
			{ "crash", (DevCrashDescription, Crash) },
			{ "levelup", (LevelUpActorDescription, LevelUp) },
			{ "player-experience", (PlayerExperienceDescription, PlayerExperience) },
			{ "power-outage", (PowerOutageDescription, PowerOutage) },
			{ "kill", (KillSelectedActorsDescription, Kill) },
			{ "dispose", (DisposeSelectedActorsDescription, Dispose) },
			{ "produce", (ProduceFromSelectedActorsDescription, Produce) },
			{ "clear-resources", (ClearResourcesDescription, ClearResources) }
		};

		World world;
		DeveloperMode developerMode;

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;

			if (world.LocalPlayer != null)
				developerMode = world.LocalPlayer.PlayerActor.Trait<DeveloperMode>();

			var console = world.WorldActor.Trait<ChatCommands>();
			var help = world.WorldActor.Trait<HelpCommand>();

			foreach (var command in commandHandlers)
			{
				console.RegisterCommand(command.Key, this);
				help.RegisterHelp(command.Key, command.Value.Description);
			}
		}

		public void InvokeCommand(string name, string arg)
		{
			if (world.LocalPlayer == null)
				return;

			if (!developerMode.Enabled)
			{
				TextNotificationsManager.Debug(FluentProvider.GetMessage(CheatsDisabled));
				return;
			}

			if (commandHandlers.TryGetValue(name, out var command))
				command.Handler(arg, world);
		}

		static void GiveCash(string arg, World world)
		{
			IssueCashDevCommand(world, "DevGiveCash", arg);
		}

		static void GiveCashAll(string arg, World world)
		{
			IssueCashDevCommand(world, "DevGiveCashAll", arg);
		}

		static void IssueCashDevCommand(World world, string command, string arg)
		{
			var giveCashOrder = new Order(command, world.LocalPlayer.PlayerActor, false);

			if (string.IsNullOrEmpty(arg))
				giveCashOrder.ExtraData = 0;
			else if (int.TryParse(arg, out var cash))
				giveCashOrder.ExtraData = (uint)cash;
			else
			{
				TextNotificationsManager.Debug(FluentProvider.GetMessage(InvalidCashAmount));
				return;
			}

			world.IssueOrder(giveCashOrder);
		}

		static void Visibility(string arg, World world)
		{
			IssueDevCommand(world, "DevVisibility");
		}

		static void VisibilityAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevVisibility", player.PlayerActor, false));
		}

		static void InstantBuild(string arg, World world)
		{
			IssueDevCommand(world, "DevFastBuild");
		}

		static void InstantBuildAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevFastBuild", player.PlayerActor, false));
		}

		static void BuildAnywhere(string arg, World world)
		{
			IssueDevCommand(world, "DevBuildAnywhere");
		}

		static void BuildAnywhereAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevBuildAnywhere", player.PlayerActor, false));
		}

		static void UnlimitedPower(string arg, World world)
		{
			IssueDevCommand(world, "DevUnlimitedPower");
		}

		static void UnlimitedPowerAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevUnlimitedPower", player.PlayerActor, false));
		}

		static void EnableTech(string arg, World world)
		{
			IssueDevCommand(world, "DevEnableTech");
		}

		static void EnableTechAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevEnableTech", player.PlayerActor, false));
		}

		static void FastCharge(string arg, World world)
		{
			IssueDevCommand(world, "DevFastCharge");
		}

		static void FastChargeAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevFastCharge", player.PlayerActor, false));
		}

		static void All(string arg, World world)
		{
			IssueDevCommand(world, "DevAll");
		}

		static void AllForAll(string arg, World world)
		{
			foreach (var player in world.Players.Where(p => !p.NonCombatant))
				world.IssueOrder(new Order("DevAll", player.PlayerActor, false));
		}

		static void Crash(string arg, World world)
		{
			throw new DevException();
		}

		static void LevelUp(string arg, World world)
		{
			foreach (var actor in world.Selection.Actors)
			{
				if (actor.IsDead)
					continue;

				var leveluporder = new Order("DevLevelUp", actor, false);
				if (int.TryParse(arg, out var level))
					leveluporder.ExtraData = (uint)level;

				if (actor.Info.HasTraitInfo<GainsExperienceInfo>())
					world.IssueOrder(leveluporder);
			}
		}

		static void PlayerExperience(string arg, World world)
		{
			if (!int.TryParse(arg, out var experience))
				return;

			foreach (var player in world.Selection.Actors.Select(a => a.Owner.PlayerActor).Distinct())
				world.IssueOrder(new Order("DevPlayerExperience", player, false) { ExtraData = (uint)experience });
		}

		static void PowerOutage(string arg, World world)
		{
			foreach (var player in world.Selection.Actors.Select(a => a.Owner.PlayerActor).Distinct())
				world.IssueOrder(new Order("PowerOutage", player, false) { ExtraData = 250 });
		}

		static void Kill(string arg, World world)
		{
			foreach (var actor in world.Selection.Actors)
			{
				if (actor.IsDead)
					continue;

				world.IssueOrder(new Order("DevKill", world.LocalPlayer.PlayerActor, Target.FromActor(actor), false) { TargetString = arg });
			}
		}

		static void Dispose(string arg, World world)
		{
			foreach (var actor in world.Selection.Actors)
			{
				if (actor.Disposed)
					continue;

				world.IssueOrder(new Order("DevDispose", world.LocalPlayer.PlayerActor, Target.FromActor(actor), false));
			}
		}

		static void Produce(string arg, World world)
		{
			foreach (var actor in world.Selection.Actors)
			{
				if (actor.IsDead)
					continue;

				world.IssueOrder(new Order("DevProduce", world.LocalPlayer.PlayerActor, Target.FromActor(actor), false) { TargetString = arg });
			}
		}

		static void ClearResources(string arg, World world)
		{
			IssueDevCommand(world, "DevClearResources");
		}

		static void IssueDevCommand(World world, string command)
		{
			world.IssueOrder(new Order(command, world.LocalPlayer.PlayerActor, false));
		}

		public class DevException : Exception { }
	}
}
