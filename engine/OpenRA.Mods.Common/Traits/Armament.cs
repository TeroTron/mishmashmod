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
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class Barrel
	{
		public WVec Offset;
		public WVec CasingOffset;
		public WVec CasingTargetOffset;
		public WAngle Yaw;
	}

	[Desc("Allows you to attach weapons to the unit (use @IdentifierSuffix for > 1)")]
	public class ArmamentInfo : PausableConditionalTraitInfo, Requires<AttackBaseInfo>
	{
		public readonly string Name = "primary";

		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Has to be defined in weapons.yaml as well.")]
		public readonly string Weapon = null;

		[Desc("Which turret (if present) should this armament be assigned to.")]
		public readonly string Turret = "primary";

		[Desc("Time (in frames) until the weapon can fire again.")]
		public readonly int FireDelay = 0;

		[Desc("Muzzle position relative to turret or body, (forward, right, up) triples.",
			"If weapon Burst = 1, it cycles through all listed offsets, otherwise the offset corresponding to current burst is used.")]
		public readonly WVec[] LocalOffset = [];

		[Desc("Muzzle yaw relative to turret or body.")]
		public readonly WAngle[] LocalYaw = [];

		[Desc("Move the turret backwards when firing.")]
		public readonly WDist Recoil = WDist.Zero;

		[Desc("Recoil recovery per-frame")]
		public readonly WDist RecoilRecovery = new(9);

		[SequenceReference]
		[Desc("Muzzle flash sequence to render")]
		public readonly string MuzzleSequence = null;

		[PaletteReference]
		[Desc("Palette to render Muzzle flash sequence in")]
		public readonly string MuzzlePalette = "effect";

		[GrantedConditionReference]
		[Desc("Condition to grant while reloading.")]
		public readonly string ReloadingCondition = null;

		[WeaponReference]
		[Desc("Has to be defined in weapons.yaml as well.")]
		public readonly string CasingWeapon = null;

		[Desc("Casing spawn position relative to turret or body, (forward, right, up) triples.")]
		public readonly WVec[] CasingSpawnLocalOffset = Array.Empty<WVec>();

		[Desc("Casing target position relative to turret or body, (forward, right, up) triples.")]
		public readonly WVec[] CasingTargetOffset = Array.Empty<WVec>();

		[Desc("Casing target position will be modified to ground level.")]
		public readonly bool CasingHitGroundLevel = true;

		public WeaponInfo WeaponInfo { get; private set; }
		public WeaponInfo CasingWeaponInfo { get; private set; }
		public WDist ModifiedRange { get; private set; }

		public readonly PlayerRelationship TargetRelationships = PlayerRelationship.Enemy;
		public readonly PlayerRelationship ForceTargetRelationships = PlayerRelationship.Enemy | PlayerRelationship.Neutral | PlayerRelationship.Ally;

		// TODO: instead of having multiple Armaments and unique AttackBase,
		// an actor should be able to have multiple AttackBases with
		// a single corresponding Armament each
		[CursorReference]
		[Desc("Cursor to display when hovering over a valid target.")]
		public readonly string Cursor = "attack";

		// TODO: same as above
		[CursorReference]
		[Desc("Cursor to display when hovering over a valid target that is outside of range.")]
		public readonly string OutsideRangeCursor = "attackoutsiderange";

		[Desc("Ammo the weapon consumes per shot.")]
		public readonly int AmmoUsage = 1;

		[Desc("Damage value for this weapon to use for Actor Stats Widget.")]
		public readonly int? Damage;

		[Desc("Spread value for this weapon to use for Actor Stats Widget.")]
		public readonly WDist? Spread;

		[Desc("Overrides the reload delay value value for this weapon to use for Actor Stats Widget.")]
		public readonly int? ReloadDelay;

		[Desc("Overrides the range value value for this weapon to use for Actor Stats Widget.")]
		public readonly WDist? Range;

		[Desc("Overrides the minimum range value value for this weapon to use for Actor Stats Widget.")]
		public readonly WDist? MinimumRange;

		public override object Create(ActorInitializer init) { return new Armament(init.Self, this); }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			var weaponToLower = Weapon.ToLowerInvariant();
			if (!rules.Weapons.TryGetValue(weaponToLower, out var weaponInfo))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{weaponToLower}'");

			WeaponInfo = weaponInfo;
			ModifiedRange = new WDist(Util.ApplyPercentageModifiers(
				WeaponInfo.Range.Length,
				ai.TraitInfos<IRangeModifierInfo>().Select(m => m.GetRangeModifierDefault())));

			if (CasingWeapon != null)
			{
				var casingweaponToLower = CasingWeapon.ToLowerInvariant();
				if (!rules.Weapons.TryGetValue(casingweaponToLower, out var casingweaponInfo))
					throw new YamlException($"Weapons Ruleset does not contain an entry '{weaponToLower}'");

				CasingWeaponInfo = casingweaponInfo;
			}

			if (WeaponInfo.Burst > 1 && WeaponInfo.BurstDelays.Length > 1 && (WeaponInfo.BurstDelays.Length != WeaponInfo.Burst - 1))
				throw new YamlException($"Weapon '{weaponToLower}' has an invalid number of BurstDelays, must be single entry or Burst - 1.");

			if (WeaponInfo.ReloadDelay <= 0)
				throw new YamlException($"Weapon '{weaponToLower}' ReloadDelay value must not be equal to or lower than 0");

			base.RulesetLoaded(rules, ai);
		}
	}

	public class Armament : PausableConditionalTrait<ArmamentInfo>, ITick
	{
		public readonly WeaponInfo Weapon;
		public readonly WeaponInfo CasingWeapon;
		public readonly Barrel[] Barrels;
		Turreted turret;
		Hovers hovers;

		BodyOrientation coords;
		INotifyBurstComplete[] notifyBurstComplete;
		List<(Actor NotifyActor, INotifyAttack Notify)> notifyAttacks;

		int conditionToken = Actor.InvalidConditionToken;

		IEnumerable<int> rangeModifiers;
		IEnumerable<int> reloadModifiers;
		IEnumerable<int> damageModifiers;
		IEnumerable<int> inaccuracyModifiers;

		int ticksSinceLastShot;
		int currentBarrel;
		readonly int barrelCount;

		readonly List<(int Ticks, int Burst, Action<int> Func)> delayedActions = [];

		public WDist Recoil;
		public int FireDelay { get; protected set; }
		public int Burst { get; protected set; }

		public Armament(Actor self, ArmamentInfo info)
			: base(info)
		{
			Actor = self;

			Weapon = info.WeaponInfo;
			CasingWeapon = info.CasingWeaponInfo;
			Burst = Weapon.Burst;

			var barrels = new List<Barrel>();
			for (var i = 0; i < info.LocalOffset.Length; i++)
			{
				barrels.Add(new Barrel
				{
					Offset = info.LocalOffset[i],
					Yaw = info.LocalYaw.Length > i ? info.LocalYaw[i] : WAngle.Zero,
					CasingOffset = info.CasingSpawnLocalOffset.Length > i ? info.CasingSpawnLocalOffset[i] : WVec.Zero,
					CasingTargetOffset = info.CasingTargetOffset.Length > i ? info.CasingTargetOffset[i] : WVec.Zero
				});
			}

			if (barrels.Count == 0)
				barrels.Add(new Barrel { Offset = WVec.Zero, Yaw = WAngle.Zero, CasingOffset = WVec.Zero, CasingTargetOffset = WVec.Zero });

			barrelCount = barrels.Count;

			Barrels = barrels.ToArray();
		}

		public virtual WDist MaxRange()
		{
			return new WDist(Util.ApplyPercentageModifiers(Weapon.Range.Length, rangeModifiers.ToArray()));
		}

		protected override void Created(Actor self)
		{
			turret = self.TraitsImplementing<Turreted>().FirstOrDefault(t => t.Name == Info.Turret);
			hovers = self.TraitOrDefault<Hovers>();
			coords = self.Trait<BodyOrientation>();
			notifyBurstComplete = self.TraitsImplementing<INotifyBurstComplete>().ToArray();
			notifyAttacks = self.TraitsImplementing<INotifyAttack>().Select(a => (self, a)).ToList();

			rangeModifiers = self.TraitsImplementing<IRangeModifier>().ToArray().Select(m => m.GetRangeModifier());
			reloadModifiers = self.TraitsImplementing<IReloadModifier>().ToArray().Select(m => m.GetReloadModifier(Info.Name));
			damageModifiers = self.TraitsImplementing<IFirepowerModifier>().ToArray().Select(m => m.GetFirepowerModifier(Info.Name));
			inaccuracyModifiers = self.TraitsImplementing<IInaccuracyModifier>().ToArray().Select(m => m.GetInaccuracyModifier());

			base.Created(self);
		}

		public void AddNotifyAttacks(Actor attacker, INotifyAttack[] notifyAttacks)
		{
			this.notifyAttacks.AddRange(notifyAttacks.Select(a => (attacker, a)));
		}

		public void RemoveNotifyAttacks(INotifyAttack[] notifyAttacks)
		{
			this.notifyAttacks.RemoveAll(pair => notifyAttacks.Any(notify => notify == pair.Notify));
		}

		void UpdateCondition(Actor self)
		{
			if (string.IsNullOrEmpty(Info.ReloadingCondition))
				return;

			var enabled = !IsTraitDisabled && IsReloading;

			if (enabled && conditionToken == Actor.InvalidConditionToken)
				conditionToken = self.GrantCondition(Info.ReloadingCondition);
			else if (!enabled && conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}

		protected virtual void Tick(Actor self)
		{
			// We need to disable conditions if IsTraitDisabled is true, so we have to update conditions before the return below.
			UpdateCondition(self);

			if (IsTraitDisabled)
				return;

			if (ticksSinceLastShot < Weapon.ReloadDelay)
				++ticksSinceLastShot;

			if (FireDelay > 0)
				--FireDelay;

			Recoil = new WDist(Math.Max(0, Recoil.Length - Info.RecoilRecovery.Length));

			for (var i = 0; i < delayedActions.Count; i++)
			{
				var x = delayedActions[i];
				if (--x.Ticks <= 0)
					x.Func(x.Burst);
				delayedActions[i] = x;
			}

			delayedActions.RemoveAll(a => a.Ticks <= 0);
		}

		void ITick.Tick(Actor self)
		{
			// Split into a protected method to allow subclassing
			Tick(self);
		}

		protected void ScheduleDelayedAction(int t, int b, Action<int> a)
		{
			if (t > 0)
				delayedActions.Add((t, b, a));
			else
				a(b);
		}

		protected virtual bool CanFire(Actor self, in Target target)
		{
			if (IsReloading || IsTraitPaused)
				return false;

			if (turret != null && !turret.HasAchievedDesiredFacing)
				return false;

			if ((!target.IsInRange(self.CenterPosition, MaxRange()))
				|| (Weapon.MinRange != WDist.Zero && target.IsInRange(self.CenterPosition, Weapon.MinRange)))
				return false;

			if (!Weapon.IsValidAgainst(target, self.World, self))
				return false;

			return true;
		}

		// Note: facing is only used by the legacy positioning code
		// The world coordinate model uses Actor.Orientation
		public virtual bool CheckFire(Actor self, IFacing facing, in Target target)
		{
			if (!CanFire(self, target))
				return false;

			if (ticksSinceLastShot >= Weapon.ReloadDelay)
				Burst = Weapon.Burst;

			ticksSinceLastShot = 0;
			do
			{
				// If Weapon.Burst == 1, cycle through all LocalOffsets, otherwise use the offset corresponding to current Burst
				currentBarrel %= barrelCount;
				var barrel = Weapon.Burst == 1 ? Barrels[currentBarrel] : Barrels[Burst % Barrels.Length];
				currentBarrel++;

				FireBarrel(self, facing, target, barrel);
				UpdateBurst(self, target);
			}
			while (FireDelay == 0 && CanFire(self, target));

			return true;
		}

		protected virtual void FireBarrel(Actor self, IFacing facing, in Target target, Barrel barrel)
		{
			foreach (var (notifyActor, notify) in notifyAttacks)
				notify.PreparingAttack(notifyActor, target, this, barrel);

			WPos MuzzlePosition() => self.CenterPosition + MuzzleOffset(self, barrel);
			WAngle MuzzleFacing() => MuzzleOrientation(self, barrel).Yaw;
			var muzzleOrientation = WRot.FromYaw(MuzzleFacing());

			var passiveTarget = Weapon.TargetActorCenter ? target.CenterPosition : target.Positions.ClosestToIgnoringPath(MuzzlePosition());
			var initialOffset = Weapon.FirstBurstTargetOffset;
			if (initialOffset != WVec.Zero)
			{
				// We want this to match Armament.LocalOffset, so we need to convert it to forward, right, up
				initialOffset = new WVec(initialOffset.Y, -initialOffset.X, initialOffset.Z);
				passiveTarget += initialOffset.Rotate(muzzleOrientation);
			}

			var followingOffset = Weapon.FollowingBurstTargetOffset;
			if (followingOffset != WVec.Zero)
			{
				// We want this to match Armament.LocalOffset, so we need to convert it to forward, right, up
				followingOffset = new WVec(followingOffset.Y, -followingOffset.X, followingOffset.Z);
				passiveTarget += ((Weapon.Burst - Burst) * followingOffset).Rotate(muzzleOrientation);
			}

			var args = new ProjectileArgs
			{
				Weapon = Weapon,
				Facing = MuzzleFacing(),
				CurrentMuzzleFacing = MuzzleFacing,

				DamageModifiers = damageModifiers.ToArray(),

				InaccuracyModifiers = inaccuracyModifiers.ToArray(),

				RangeModifiers = rangeModifiers.ToArray(),

				Source = MuzzlePosition(),
				CurrentSource = MuzzlePosition,
				SourceActor = self,
				PassiveTarget = passiveTarget,
				GuidedTarget = target
			};

			ProjectileArgs argsCasing = null;
			if (CasingWeapon != null)
			{
				WPos CasingSpawnPosition() => self.CenterPosition + CasingSpawnOffset(self, barrel);

				var casingHitPosition = self.CenterPosition + CasingHitOffset(self, barrel);
				casingHitPosition = Info.CasingHitGroundLevel ?
					casingHitPosition - new WVec(0, 0, self.World.Map.DistanceAboveTerrain(casingHitPosition).Length) :
					casingHitPosition;

				WAngle CasingFireFacing() => (casingHitPosition - CasingSpawnPosition()).Yaw;

				argsCasing = new ProjectileArgs
				{
					Weapon = CasingWeapon,
					Facing = CasingFireFacing(),
					CurrentMuzzleFacing = CasingFireFacing,

					DamageModifiers = damageModifiers.ToArray(),

					InaccuracyModifiers = inaccuracyModifiers.ToArray(),

					RangeModifiers = rangeModifiers.ToArray(),

					Source = CasingSpawnPosition(),
					CurrentSource = CasingSpawnPosition,
					SourceActor = self,
					PassiveTarget = casingHitPosition
				};
			}

			// Lambdas can't use 'in' variables, so capture a copy for later
			var delayedTarget = target;
			ScheduleDelayedAction(Info.FireDelay, Burst, (burst) =>
			{
				if (args.Weapon.Projectile != null)
				{
					var projectile = args.Weapon.Projectile.Create(args);
					if (projectile != null)
						self.World.Add(projectile);

					if (args.Weapon.Report != null && args.Weapon.Report.Length > 0)
					{
						var pos = self.CenterPosition;
						if (args.Weapon.AudibleThroughFog || (!self.World.ShroudObscures(pos) && !self.World.FogObscures(pos)))
							Game.Sound.Play(SoundType.World, args.Weapon.Report, self.World, pos, null, args.Weapon.SoundVolume);
					}

					if (burst == args.Weapon.Burst && args.Weapon.StartBurstReport != null && args.Weapon.StartBurstReport.Length > 0)
					{
						var pos = self.CenterPosition;
						if (args.Weapon.AudibleThroughFog || (!self.World.ShroudObscures(pos) && !self.World.FogObscures(pos)))
							Game.Sound.Play(SoundType.World, args.Weapon.StartBurstReport, self.World, pos, null, args.Weapon.SoundVolume);
					}

					if (argsCasing != null)
					{
						var projectileCasing = argsCasing.Weapon.Projectile.Create(argsCasing);
						if (projectileCasing != null)
							self.World.Add(projectileCasing);
					}

					Recoil = Info.Recoil;
				}

				foreach (var (notifyActor, notify) in notifyAttacks)
					notify.Attacking(notifyActor, delayedTarget, this, barrel);
			});
		}

		protected virtual void UpdateBurst(Actor self, in Target target)
		{
			if (--Burst > 0)
			{
				if (Weapon.BurstDelays.Length == 1)
					FireDelay = Weapon.BurstDelays[0];
				else
					FireDelay = Weapon.BurstDelays[Weapon.Burst - (Burst + 1)];
			}
			else
			{
				var modifiers = reloadModifiers.ToArray();
				FireDelay = Util.ApplyPercentageModifiers(Weapon.ReloadDelay, modifiers);
				if (FireDelay <= 0)
					FireDelay = 1;

				Burst = Weapon.Burst;

				if (Weapon.AfterFireSound != null && Weapon.AfterFireSound.Length > 0)
				{
					ScheduleDelayedAction(Weapon.AfterFireSoundDelay, Burst, (burst) =>
					{
						var pos = self.CenterPosition;
						if (Weapon.AudibleThroughFog || (!self.World.ShroudObscures(pos) && !self.World.FogObscures(pos)))
							Game.Sound.Play(SoundType.World, Weapon.AfterFireSound, self.World, pos, null, Weapon.SoundVolume);
					});
				}

				foreach (var nbc in notifyBurstComplete)
					nbc.FiredBurst(self, target, this);
			}
		}

		public virtual bool IsReloading => FireDelay > 0 || IsTraitDisabled;

		public WVec MuzzleOffset(Actor self, Barrel b)
		{
			return CalculateFireEffectOffset(self, b.Offset);
		}

		public WVec CasingSpawnOffset(Actor self, Barrel b)
		{
			return CalculateFireEffectOffset(self, b.CasingOffset);
		}

		public WVec CasingHitOffset(Actor self, Barrel b)
		{
			return CalculateFireEffectOffset(self, b.CasingTargetOffset);
		}

		protected virtual WVec CalculateFireEffectOffset(Actor self, WVec offset)
		{
			// Weapon offset in turret coordinates
			var effectOffset = offset + new WVec(-Recoil, WDist.Zero, WDist.Zero);

			if (hovers != null)
				effectOffset += hovers.WorldVisualOffset;

			// Turret coordinates to body coordinates
			var bodyOrientation = coords.QuantizeOrientation(self.Orientation);
			if (turret != null)
				effectOffset = effectOffset.Rotate(turret.WorldOrientation) + turret.Offset.Rotate(bodyOrientation);
			else
				effectOffset = effectOffset.Rotate(bodyOrientation);

			// Body coordinates to world coordinates
			return coords.LocalToWorld(effectOffset);
		}

		public WRot MuzzleOrientation(Actor self, Barrel b)
		{
			return CalculateMuzzleOrientation(self, b);
		}

		protected virtual WRot CalculateMuzzleOrientation(Actor self, Barrel b)
		{
			return WRot.FromYaw(b.Yaw).Rotate(turret?.WorldOrientation ?? self.Orientation);
		}

		public Actor Actor { get; }
	}
}
