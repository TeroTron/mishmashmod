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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Attach this to the player actor.")]
	public class DeveloperModeInfo : TraitInfo, ILobbyOptions
	{
		[FluentReference]
		[Desc("Descriptive label for the developer mode checkbox in the lobby.")]
		public readonly string CheckboxLabel = "checkbox-debug-menu.label";

		[FluentReference]
		[Desc("Tooltip description for the developer mode checkbox in the lobby.")]
		public readonly string CheckboxDescription = "checkbox-debug-menu.description";

		[Desc("Default value of the developer mode checkbox in the lobby.")]
		public readonly bool CheckboxEnabled = false;

		[Desc("Prevent the developer mode state from being changed in the lobby.")]
		public readonly bool CheckboxLocked = false;

		[Desc("Whether to display the developer mode checkbox in the lobby.")]
		public readonly bool CheckboxVisible = true;

		[Desc("Display order for the developer mode checkbox in the lobby.")]
		public readonly int CheckboxDisplayOrder = 0;

		[Desc("Default cash bonus granted by the give cash cheat.")]
		public readonly int Cash = 20000;

		[Desc("Growth steps triggered by the grow resources button.")]
		public readonly int ResourceGrowth = 100;

		[Desc("Enable the fast build cheat by default.")]
		public readonly bool FastBuild;

		[Desc("Enable the fast support powers cheat by default.")]
		public readonly bool FastCharge;

		[Desc("Enable the disable visibility cheat by default.")]
		public readonly bool DisableShroud;

		[Desc("Enable the unlimited power cheat by default.")]
		public readonly bool UnlimitedPower;

		[Desc("Enable the build anywhere cheat by default.")]
		public readonly bool BuildAnywhere;

		[Desc("Enable the path debug overlay by default.")]
		public readonly bool PathDebug;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, "cheats",
				CheckboxLabel, CheckboxDescription, CheckboxVisible, CheckboxDisplayOrder, CheckboxEnabled, CheckboxLocked);
		}

		public override object Create(ActorInitializer init) { return new DeveloperMode(this); }
	}

	public class DeveloperMode : IResolveOrder, ISync, INotifyCreated, IUnlocksRenderPlayer
	{
		[FluentReference("cheat", "player", "suffix")]
		const string CheatUsed = "notification-cheat-used";

		[FluentReference("actor")]
		const string InvalidActorName = "notification-invalid-actor-name";

		[FluentReference("actor")]
		const string UnbuildableActorName = "notification-unbuildable-actor-name";

		readonly DeveloperModeInfo info;
		public bool Enabled { get; private set; }

		[Sync]
		bool fastCharge;

		[Sync]
		bool allTech;

		[Sync]
		bool fastBuild;

		[Sync]
		bool disableShroud;

		[Sync]
		bool pathDebug;

		[Sync]
		bool unlimitedPower;

		[Sync]
		bool buildAnywhere;

		public bool FastCharge => Enabled && fastCharge;
		public bool AllTech => Enabled && allTech;
		public bool FastBuild => Enabled && fastBuild;
		public bool DisableShroud => Enabled && disableShroud;
		public bool PathDebug => Enabled && pathDebug;
		public bool UnlimitedPower => Enabled && unlimitedPower;
		public bool BuildAnywhere => Enabled && buildAnywhere;

		bool enableAll;

		public DeveloperMode(DeveloperModeInfo info)
		{
			this.info = info;
			fastBuild = info.FastBuild;
			fastCharge = info.FastCharge;
			disableShroud = info.DisableShroud;
			pathDebug = info.PathDebug;
			unlimitedPower = info.UnlimitedPower;
			buildAnywhere = info.BuildAnywhere;
		}

		void INotifyCreated.Created(Actor self)
		{
			Enabled = self.World.LobbyInfo.NonBotPlayers.Count() == 1 || self.World.LobbyInfo.GlobalSettings
				.OptionOrDefault("cheats", info.CheckboxEnabled);
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (!Enabled)
				return;

			var debugSuffix = "";
			switch (order.OrderString)
			{
				case "DevAll":
				{
					enableAll ^= true;
					allTech = fastCharge = fastBuild = disableShroud = unlimitedPower = buildAnywhere = enableAll;

					if (enableAll)
					{
						var amount = order.ExtraData != 0 ? (int)order.ExtraData : info.Cash;
						self.Trait<PlayerResources>().ChangeCash(amount);
					}

					self.Owner.Shroud.Disabled = DisableShroud;
					if (self.World.LocalPlayer == self.Owner)
						self.World.RenderPlayer = DisableShroud ? null : self.Owner;

					break;
				}

				case "DevEnableTech":
				{
					allTech ^= true;
					break;
				}

				case "DevFastCharge":
				{
					fastCharge ^= true;
					break;
				}

				case "DevFastBuild":
				{
					fastBuild ^= true;
					break;
				}

				case "DevGiveCash":
				{
					var amount = order.ExtraData != 0 ? (int)order.ExtraData : info.Cash;
					self.Trait<PlayerResources>().ChangeCash(amount);

					debugSuffix = $" ({amount} credits)";
					break;
				}

				case "DevGiveCashAll":
				{
					var amount = order.ExtraData != 0 ? (int)order.ExtraData : info.Cash;
					var receivingPlayers = self.World.Players.Where(p => p.Playable);

					foreach (var player in receivingPlayers)
						player.PlayerActor.Trait<PlayerResources>().ChangeCash(amount);

					debugSuffix = $" ({amount} credits)";
					break;
				}

				case "DevGrowResources":
				{
					foreach (var a in self.World.ActorsWithTrait<ISeedableResource>())
						for (var i = 0; i < info.ResourceGrowth; i++)
							a.Trait.Seed(a.Actor);

					break;
				}

				case "DevVisibility":
				{
					disableShroud ^= true;
					self.Owner.Shroud.Disabled = DisableShroud;
					if (self.World.LocalPlayer == self.Owner)
						self.World.RenderPlayer = DisableShroud ? null : self.Owner;

					break;
				}

				case "DevPathDebug":
				{
					pathDebug ^= true;
					break;
				}

				case "DevGiveExploration":
				{
					self.Owner.Shroud.ExploreAll();
					break;
				}

				case "DevResetExploration":
				{
					self.Owner.Shroud.ResetExploration();
					break;
				}

				case "DevUnlimitedPower":
				{
					unlimitedPower ^= true;
					break;
				}

				case "DevBuildAnywhere":
				{
					buildAnywhere ^= true;
					break;
				}

				case "DevPlayerExperience":
				{
					self.Owner.PlayerActor.TraitOrDefault<PlayerExperience>()?.GiveExperience((int)order.ExtraData);
					break;
				}

				case "DevKill":
				{
					if (order.Target.Type != TargetType.Actor)
						break;

					var actor = order.Target.Actor;
					var args = order.TargetString.Split(' ');
					var damageTypes = BitSet<DamageType>.FromStringsNoAlloc(args);

					actor.Kill(actor, damageTypes);
					break;
				}

				case "DevDispose":
				{
					if (order.Target.Type != TargetType.Actor)
						break;

					order.Target.Actor.Dispose();
					break;
				}

				case "DevProduce":
				{
					if (order.Target.Type != TargetType.Actor)
						break;

					var args = order.TargetString.Split(' ');
					var producer = order.Target.Actor;
					var production = producer.TraitsImplementing<Production>().FirstOrDefault(p => !p.IsTraitDisabled && !p.IsTraitPaused);
					var actors = self.World.Map.Rules.Actors;
					var actorToProduce = actors.Keys.Contains(args[0]) ? actors[args[0]] : null;
					var buildable = actorToProduce?.TraitInfos<BuildableInfo>().Count > 0;

					if (production != null && actorToProduce != null && buildable)
					{
						var faction = args.Length > 1 ? args[1] : BuildableInfo.GetInitialFaction(actorToProduce, production.Faction);
						var inits = new TypeDictionary
						{
							new OwnerInit(producer.Owner),
							new FactionInit(faction)
						};

						producer.QueueActivity(new WaitFor(() => production.Produce(producer, actorToProduce, null, inits, 0)));
					}

					if (actorToProduce == null)
						TextNotificationsManager.Debug(FluentProvider.GetMessage(InvalidActorName, "actor", args[0]));
					else if (!buildable)
						TextNotificationsManager.Debug(FluentProvider.GetMessage(UnbuildableActorName, "actor", args[0]));

					break;
				}

				case "DevClearResources":
				{
					var resLayer = self.World.WorldActor.TraitOrDefault<IResourceLayer>();
					foreach (var cell in self.World.Map.ProjectedCells.ToArray())
						resLayer.ClearResources(((MPos)cell).ToCPos(self.World.Map.Grid.Type));

					break;
				}

				default:
					return;
			}

			TextNotificationsManager.Debug(FluentProvider.GetMessage(CheatUsed,
				"cheat", order.OrderString,
				"player", self.Owner.ResolvedPlayerName,
				"suffix", debugSuffix));
		}

		bool IUnlocksRenderPlayer.RenderPlayerUnlocked => Enabled;
	}
}
