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

namespace OpenRA.Mods.Common.Traits.Sound
{
	public class SoundOnDamageTransitionInfo : TraitInfo
	{
		[Desc("Play a random sound from this list when damaged.")]
		public readonly string[] DamagedSounds = [];

		[Desc("Play a random sound from this list when destroyed.")]
		public readonly string[] DestroyedSounds = [];

		[Desc("DamageType(s) that trigger the sounds. Leave empty to always trigger a sound.")]
		public readonly BitSet<DamageType> DamageTypes = default;

		[Desc("Do the sounds play under shroud or fog.")]
		public readonly bool AudibleThroughFog = false;

		[Desc("Volume the DamagedSounds and DestroyedSounds played at.")]
		public readonly float SoundVolume = 1f;

		public override object Create(ActorInitializer init) { return new SoundOnDamageTransition(this); }
	}

	public class SoundOnDamageTransition : INotifyDamageStateChanged
	{
		readonly SoundOnDamageTransitionInfo info;

		public SoundOnDamageTransition(SoundOnDamageTransitionInfo info)
		{
			this.info = info;
		}

		void INotifyDamageStateChanged.DamageStateChanged(Actor self, AttackInfo e)
		{
			if (!info.DamageTypes.IsEmpty && !e.Damage.DamageTypes.Overlaps(info.DamageTypes))
				return;

			var rand = Game.CosmeticRandom;
			var pos = self.CenterPosition;

			if (info.AudibleThroughFog || (!self.World.ShroudObscures(pos) && !self.World.FogObscures(pos)))
			{
				if (e.DamageState == DamageState.Dead)
				{
					var sound = info.DestroyedSounds.RandomOrDefault(rand);
					Game.Sound.Play(SoundType.World, sound, pos, info.SoundVolume);
				}
				else if (e.DamageState >= DamageState.Heavy && e.PreviousDamageState < DamageState.Heavy)
				{
					var sound = info.DamagedSounds.RandomOrDefault(rand);
					Game.Sound.Play(SoundType.World, sound, pos, info.SoundVolume);
				}
			}
		}
	}
}
