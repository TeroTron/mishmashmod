﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	using FrozenActorAction = Action<FrozenUnderFogUpdatedByGpsAS, FrozenActorLayer, GpsASWatcher, FrozenActor>;

	[Desc("Updates frozen actors of actors that change owners, are sold or die whilst having an active GPS power.")]
	public class FrozenUnderFogUpdatedByGpsASInfo : TraitInfo, Requires<FrozenUnderFogInfo>
	{
		public override object Create(ActorInitializer init) { return new FrozenUnderFogUpdatedByGpsAS(init); }
	}

	public class FrozenUnderFogUpdatedByGpsAS : INotifyOwnerChanged, INotifyActorDisposing, IOnGpsASRefreshed
	{
		static readonly FrozenActorAction Refresh = (fufubg, fal, gps, fa) =>
		{
			// Refreshes the visual state of the frozen actor, so ownership changes can be seen.
			// This only makes sense if the frozen actor has already been revealed (i.e. has renderables)
			if (fa.HasRenderables)
			{
				fa.RefreshState();
				fa.NeedRenderables = true;
			}
		};
		static readonly FrozenActorAction Remove = (fufubg, fal, gps, fa) =>
		{
			// Removes the frozen actor. Once done, we no longer need to track GPS updates.
			fa.Invalidate();
			fal.Remove(fa);
			gps.UnregisterForOnGpsRefreshed(fufubg.self, fufubg);
		};

		class Traits
		{
			public readonly FrozenActorLayer FrozenActorLayer;
			public readonly GpsASWatcher GpsWatcher;
			public Traits(Player player, FrozenUnderFogUpdatedByGpsAS frozenUnderFogUpdatedByGps)
			{
				FrozenActorLayer = player.FrozenActorLayer;
				GpsWatcher = player.PlayerActor.TraitOrDefault<GpsASWatcher>();
				GpsWatcher.RegisterForOnGpsRefreshed(frozenUnderFogUpdatedByGps.self, frozenUnderFogUpdatedByGps);
			}
		}

		readonly PlayerDictionary<Traits> traits;
		readonly Actor self;

		public FrozenUnderFogUpdatedByGpsAS(ActorInitializer init)
		{
			self = init.Self;
			traits = new PlayerDictionary<Traits>(init.World, player => new Traits(player, this));
		}

		public void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			ActOnFrozenActorsForAllPlayers(Refresh);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			ActOnFrozenActorsForAllPlayers(Remove);
		}

		public void OnGpsASRefresh(Actor self, Player player)
		{
			if (self.IsDead)
				ActOnFrozenActorForPlayer(player, Remove);
			else
				ActOnFrozenActorForPlayer(player, Refresh);
		}

		void ActOnFrozenActorsForAllPlayers(FrozenActorAction action)
		{
			for (var playerIndex = 0; playerIndex < traits.Count; playerIndex++)
				ActOnFrozenActorForTraits(traits[playerIndex], action);
		}

		void ActOnFrozenActorForPlayer(Player player, FrozenActorAction action)
		{
			ActOnFrozenActorForTraits(traits[player], action);
		}

		void ActOnFrozenActorForTraits(Traits t, FrozenActorAction action)
		{
			if (t.FrozenActorLayer == null || t.GpsWatcher == null ||
				!t.GpsWatcher.Granted || !t.GpsWatcher.GrantedAllies)
				return;

			var fa = t.FrozenActorLayer.FromID(self.ActorID);
			if (fa == null)
				return;

			action(this, t.FrozenActorLayer, t.GpsWatcher, fa);
		}
	}
}
