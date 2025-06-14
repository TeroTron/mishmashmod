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

using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class TakeOff : Activity
	{
		readonly Aircraft aircraft;

		public TakeOff(Actor self)
		{
			ActivityType = ActivityType.Move;
			aircraft = self.Trait<Aircraft>();
		}

		protected override void OnFirstRun(Actor self)
		{
			if (aircraft.ForceLanding)
				return;

			if (!aircraft.HasInfluence())
				return;

			// We are taking off, so remove influence in ground cells.
			aircraft.RemoveInfluence();

			if (self.World.Map.DistanceAboveTerrain(aircraft.CenterPosition).Length > aircraft.Info.MinAirborneAltitude)
				return;

			if (aircraft.Info.TakeoffSounds.Length > 0)
			{
				var pos = aircraft.CenterPosition;
				if (aircraft.Info.AudibleThroughFog || (!self.World.ShroudObscures(pos) && !self.World.FogObscures(pos)))
					Game.Sound.Play(SoundType.World, aircraft.Info.TakeoffSounds, self.World, pos, null, aircraft.Info.SoundVolume);
			}

			foreach (var notify in self.TraitsImplementing<INotifyTakeOff>())
				notify.TakeOff(self);
		}

		public override bool Tick(Actor self)
		{
			// Refuse to take off if it would land immediately again.
			if (aircraft.ForceLanding)
			{
				Cancel(self);
				return true;
			}

			var dat = self.World.Map.DistanceAboveTerrain(aircraft.CenterPosition);
			if (dat < aircraft.Info.CruiseAltitude)
			{
				// If we're a VTOL, rise before flying forward
				if (aircraft.Info.VTOL)
				{
					Fly.VerticalTakeOffOrLandTick(self, aircraft, aircraft.Facing, aircraft.Info.CruiseAltitude);
					return false;
				}

				Fly.FlyTick(self, aircraft, aircraft.Facing, aircraft.Info.CruiseAltitude);
				return false;
			}

			return true;
		}
	}
}
