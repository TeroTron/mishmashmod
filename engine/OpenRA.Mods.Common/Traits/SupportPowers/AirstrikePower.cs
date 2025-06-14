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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Support power that spawns a group of aircraft and orders them to deliver an airstrike.")]
	public class AirstrikePowerInfo : DirectionalSupportPowerInfo
	{
		[FieldLoader.Require]
		[Desc("Aircraft used to deliver the airstrike.")]
		public readonly Dictionary<int, string> UnitTypes = new();

		[FieldLoader.Require]
		[Desc("Number of aircraft to use in an airstrike formation.")]
		public readonly Dictionary<int, int> SquadSizes = new();

		[Desc("Offset vector between the aircraft in a formation.")]
		public readonly WVec SquadOffset = new(-1536, 1536, 0);

		[Desc("Number of different possible facings of the aircraft (used only for choosing a random direction to spawn from.)")]
		public readonly int QuantizedFacings = 32;

		[Desc("Additional distance from the map edge to spawn the aircraft.")]
		public readonly WDist Cordon = new(5120);

		[ActorReference]
		[Desc("Actor to spawn when the aircraft start attacking.")]
		public readonly string CameraActor = null;

		[Desc("Amount of time to keep the camera alive after the aircraft have finished attacking.")]
		public readonly int CameraRemoveDelay = 25;

		[Desc("Weapon range offset to apply during the beacon clock calculation.")]
		public readonly WDist BeaconDistanceOffset = WDist.FromCells(6);

		public override object Create(ActorInitializer init) { return new AirstrikePower(init.Self, this); }
	}

	public class AirstrikePower : DirectionalSupportPower
	{
		readonly AirstrikePowerInfo info;

		public AirstrikePower(Actor self, AirstrikePowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);

			var facing = info.UseDirectionalTarget && order.ExtraData != uint.MaxValue ? (WAngle?)WAngle.FromFacing((int)order.ExtraData) : null;
			SendAirstrike(self, order.Target.CenterPosition, facing);
		}

		public Actor[] SendAirstrike(Actor self, WPos target, WAngle? facing = null)
		{
			var level = GetLevel();
			if (level == 0)
				return Array.Empty<Actor>();

			var aircraft = new List<Actor>();
			if (!facing.HasValue)
				facing = new WAngle(1024 * self.World.SharedRandom.Next(info.QuantizedFacings) / info.QuantizedFacings);

			var altitude = self.World.Map.Rules.Actors[info.UnitTypes.First(ut => ut.Key == level).Value].TraitInfo<AircraftInfo>().CruiseAltitude.Length;
			var attackRotation = WRot.FromYaw(facing.Value);
			var delta = new WVec(0, -1024, 0).Rotate(attackRotation);
			target += new WVec(0, 0, altitude);
			var startEdge = target - (self.World.Map.DistanceToEdge(target, -delta) + info.Cordon).Length * delta / 1024;
			var finishEdge = target + (self.World.Map.DistanceToEdge(target, delta) + info.Cordon).Length * delta / 1024;

			Actor camera = null;
			Beacon beacon = null;
			var aircraftInRange = new Dictionary<Actor, bool>();

			void OnEnterRange(Actor a)
			{
				// Spawn a camera and remove the beacon when the first plane enters the target area
				if (info.CameraActor != null && camera == null && !aircraftInRange.Any(kv => kv.Value))
				{
					self.World.AddFrameEndTask(w =>
					{
						camera = w.CreateActor(info.CameraActor,
						[
							new LocationInit(self.World.Map.CellContaining(target)),
							new OwnerInit(self.Owner),
						]);
					});
				}

				RemoveBeacon(beacon);

				aircraftInRange[a] = true;
			}

			void OnExitRange(Actor a)
			{
				aircraftInRange[a] = false;

				// Remove the camera when the final plane leaves the target area
				if (!aircraftInRange.Any(kv => kv.Value))
					RemoveCamera(camera);
			}

			void OnRemovedFromWorld(Actor a)
			{
				aircraftInRange[a] = false;

				// Checking for attack range is not relevant here because
				// aircraft may be shot down before entering the range.
				// If at the map's edge, they may be removed from world before leaving.
				if (aircraftInRange.All(kv => !kv.Key.IsInWorld))
				{
					RemoveCamera(camera);
					RemoveBeacon(beacon);
				}
			}

			// Create the actors immediately so they can be returned
			var squadSize = info.SquadSizes.First(ss => ss.Key == level).Value;
			for (var i = -squadSize / 2; i <= squadSize / 2; i++)
			{
				// Even-sized squads skip the lead plane
				if (i == 0 && (squadSize & 1) == 0)
					continue;

				// Includes the 90 degree rotation between body and world coordinates
				var so = info.SquadOffset;
				var spawnOffset = new WVec(i * so.Y, -Math.Abs(i) * so.X, 0).Rotate(attackRotation);
				var targetOffset = new WVec(i * so.Y, 0, 0).Rotate(attackRotation);
				var a = self.World.CreateActor(false, info.UnitTypes.First(ut => ut.Key == level).Value,
				[
					new CenterPositionInit(startEdge + spawnOffset),
					new OwnerInit(self.Owner),
					new FacingInit(facing.Value),
				]);

				aircraft.Add(a);
				aircraftInRange.Add(a, false);

				var attack = a.Trait<AttackBomber>();
				attack.SetTarget(target + targetOffset);
				attack.OnEnteredAttackRange += OnEnterRange;
				attack.OnExitedAttackRange += OnExitRange;
				attack.OnRemovedFromWorld += OnRemovedFromWorld;
			}

			self.World.AddFrameEndTask(w =>
			{
				PlayLaunchSounds();

				var j = 0;
				Actor distanceTestActor = null;
				for (var i = -squadSize / 2; i <= squadSize / 2; i++)
				{
					// Even-sized squads skip the lead plane
					if (i == 0 && (squadSize & 1) == 0)
						continue;

					// Includes the 90 degree rotation between body and world coordinates
					var so = info.SquadOffset;
					var spawnOffset = new WVec(i * so.Y, -Math.Abs(i) * so.X, 0).Rotate(attackRotation);

					var a = aircraft[j++];
					w.Add(a);

					a.QueueActivity(new Fly(a, Target.FromPos(target + spawnOffset)));
					a.QueueActivity(new Fly(a, Target.FromPos(finishEdge + spawnOffset)));
					a.QueueActivity(new RemoveSelf());
					distanceTestActor = a;
				}

				if (Info.DisplayBeacon)
				{
					var distance = (target - startEdge).HorizontalLength;

					beacon = new Beacon(
						self.Owner,
						target - new WVec(0, 0, altitude),
						Info.BeaconPaletteIsPlayerPalette,
						Info.BeaconPalette,
						Info.BeaconImage,
						Info.BeaconPosters.First(bp => bp.Key == level).Value,
						Info.BeaconPosterPalette,
						Info.BeaconSequence,
						Info.ArrowSequence,
						Info.CircleSequence,
						Info.ClockSequence,
						() => 1 - ((distanceTestActor.CenterPosition - target).HorizontalLength - info.BeaconDistanceOffset.Length) * 1f / distance,
						Info.BeaconDelay);

					w.Add(beacon);
				}
			});

			return aircraft.ToArray();
		}

		void RemoveCamera(Actor camera)
		{
			if (camera == null)
				return;

			camera.QueueActivity(new Wait(info.CameraRemoveDelay));
			camera.QueueActivity(new RemoveSelf());
		}

		void RemoveBeacon(Beacon beacon)
		{
			if (beacon == null)
				return;

			Self.World.AddFrameEndTask(w => w.Remove(beacon));
		}
	}
}
