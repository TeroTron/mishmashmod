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

using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("This actor is crushable.")]
	sealed class CrushableInfo : ConditionalTraitInfo
	{
		[Desc("Sound to play when being crushed.")]
		public readonly string CrushSound = null;
		[Desc("Do the sounds play under shroud or fog.")]
		public readonly bool AudibleThroughFog = false;
		[Desc("Volume the CrushSound played at.")]
		public readonly float SoundVolume = 1f;
		[Desc("Which crush classes does this actor belong to.")]
		public readonly BitSet<CrushClass> CrushClasses = new("infantry");
		[Desc("Probability of mobile actors noticing and evading a crush attempt.")]
		public readonly int WarnProbability = 75;
		[Desc("Will friendly units just crush me instead of pathing around.")]
		public readonly bool CrushedByFriendlies = false;

		public override object Create(ActorInitializer init) { return new Crushable(init.Self, this); }
	}

	sealed class Crushable : ConditionalTrait<CrushableInfo>, ICrushable, INotifyCrushed
	{
		readonly Actor self;

		public Crushable(Actor self, CrushableInfo info)
			: base(info)
		{
			this.self = self;
		}

		void INotifyCrushed.WarnCrush(Actor self, Actor crusher, BitSet<CrushClass> crushClasses)
		{
			if (!CrushableInner(crushClasses, crusher.Owner))
				return;

			var mobile = self.TraitOrDefault<Mobile>();
			if (mobile != null && self.World.SharedRandom.Next(100) <= Info.WarnProbability)
				self.QueueActivity(false, new Nudge(crusher));
		}

		void INotifyCrushed.OnCrush(Actor self, Actor crusher, BitSet<CrushClass> crushClasses)
		{
			if (!CrushableInner(crushClasses, crusher.Owner))
				return;

			var pos = crusher.CenterPosition;
			if (Info.AudibleThroughFog || (!self.World.ShroudObscures(pos) && !self.World.FogObscures(pos)))
				Game.Sound.Play(SoundType.World, Info.CrushSound, pos, Info.SoundVolume);

			var crusherMobile = crusher.TraitOrDefault<Mobile>();
			self.Kill(crusher, crusherMobile != null ? crusherMobile.Info.LocomotorInfo.CrushDamageTypes : default);
		}

		bool ICrushable.CrushableBy(Actor self, Actor crusher, BitSet<CrushClass> crushClasses)
		{
			return CrushableInner(crushClasses, crusher.Owner);
		}

		LongBitSet<PlayerBitMask> ICrushable.CrushableBy(Actor self, BitSet<CrushClass> crushClasses)
		{
			if (IsTraitDisabled || !Info.CrushClasses.Overlaps(crushClasses))
				return self.World.NoPlayersMask;

			return Info.CrushedByFriendlies ? self.World.AllPlayersMask : self.World.AllPlayersMask.Except(self.Owner.AlliedPlayersMask);
		}

		bool CrushableInner(BitSet<CrushClass> crushClasses, Player crushOwner)
		{
			if (IsTraitDisabled)
				return false;

			if (!Info.CrushedByFriendlies && crushOwner.IsAlliedWith(self.Owner))
				return false;

			return Info.CrushClasses.Overlaps(crushClasses);
		}

		protected override void TraitEnabled(Actor self)
		{
			self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}

		protected override void TraitDisabled(Actor self)
		{
			self.World.ActorMap.UpdateOccupiedCells(self.OccupiesSpace);
		}
	}
}
