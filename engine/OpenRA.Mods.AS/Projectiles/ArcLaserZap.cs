﻿#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.AS.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Projectiles
{
	[Desc("Not a sprite, but an engine effect.")]
	public class ArcLaserZapInfo : IProjectileInfo
	{
		[Desc("The width of the zap.")]
		public readonly WDist Width = new(86);

		[Desc("Equivalent to sequence ZOffset. Controls Z sorting.")]
		public readonly int ZOffset = 0;

		public readonly int Duration = 10;

		[Desc("The angle of the arc of the beam.")]
		public readonly WAngle Angle = WAngle.FromDegrees(30);

		[Desc("Controls how fine-grained the resulting arc should be.")]
		public readonly int QuantizedSegments = 32;

		public readonly bool UsePlayerColor = false;

		[Desc("Color of the beam.")]
		public readonly Color Color = Color.Red;

		[Desc("Beam follows the target.")]
		public readonly bool TrackTarget = true;

		[Desc("Beam follows the source.")]
		public readonly bool TrackSource = true;

		[Desc("Maximum offset at the maximum range.")]
		public readonly WDist Inaccuracy = WDist.Zero;

		[Desc("Beam can be blocked.")]
		public readonly bool Blockable = false;

		[Desc("Impact animation.")]
		public readonly string HitAnim = null;

		[Desc("Sequence of impact animation to use.")]
		[SequenceReference(nameof(HitAnim), allowNullImage: true)]
		public readonly string HitAnimSequence = "idle";

		[PaletteReference]
		public readonly string HitAnimPalette = "effect";

		public IProjectile Create(ProjectileArgs args)
		{
			var c = UsePlayerColor ? args.SourceActor.Owner.Color : Color;
			return new ArcLaserZap(this, args, c);
		}
	}

	public class ArcLaserZap : IProjectile, ISync
	{
		readonly ProjectileArgs args;
		readonly ArcLaserZapInfo info;
		readonly Animation hitanim;
		readonly Color color;

		[Sync]
		WPos source;

		int ticks = 0;
		bool doneDamage;
		bool animationComplete;

		[Sync]
		WPos target;

		public ArcLaserZap(ArcLaserZapInfo info, ProjectileArgs args, Color color)
		{
			this.args = args;
			this.info = info;
			this.color = color;
			target = args.PassiveTarget;
			source = args.Source;

			if (info.Inaccuracy.Length > 0)
			{
				var inaccuracy = Common.Util.ApplyPercentageModifiers(info.Inaccuracy.Length, args.InaccuracyModifiers);
				var maxOffset = inaccuracy * (target - source).Length / args.Weapon.Range.Length;
				target += WVec.FromPDF(args.SourceActor.World.SharedRandom, 2) * maxOffset / 1024;
			}

			if (!string.IsNullOrEmpty(info.HitAnim))
				hitanim = new Animation(args.SourceActor.World, info.HitAnim);
		}

		public void Tick(World world)
		{
			if (info.TrackSource)
				source = args.CurrentSource();

			// Beam tracks target
			if (info.TrackTarget && args.GuidedTarget.IsValidFor(args.SourceActor))
				target = args.Weapon.TargetActorCenter ? args.GuidedTarget.CenterPosition : args.GuidedTarget.Positions.ClosestToIgnoringPath(source);

			// Check for blocking actors
			if (info.Blockable && BlocksProjectiles.AnyBlockingActorsBetween(world, args.SourceActor.Owner, source, target,
				info.Width, out var blockedPos))
				target = blockedPos;

			if (!doneDamage)
			{
				if (hitanim != null)
					hitanim.PlayThen(info.HitAnimSequence, () => animationComplete = true);
				else
					animationComplete = true;

				var warheadArgs = new WarheadArgs(args)
				{
					ImpactOrientation = new WRot(WAngle.Zero, Common.Util.GetVerticalAngle(source, target), args.CurrentMuzzleFacing()),
					ImpactPosition = target,
				};

				args.Weapon.Impact(Target.FromPos(target), warheadArgs);
				doneDamage = true;
			}

			hitanim?.Tick();

			if (++ticks >= info.Duration && animationComplete)
				world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (wr.World.FogObscures(target) &&
				wr.World.FogObscures(source))
				yield break;

			if (ticks < info.Duration)
			{
				var rc = Color.FromArgb((info.Duration - ticks) * color.A / info.Duration, color);
				yield return new ArcRenderable(source, target, info.ZOffset, info.Angle, rc, info.Width, info.QuantizedSegments);
			}

			if (hitanim != null)
				foreach (var r in hitanim.Render(target, wr.Palette(info.HitAnimPalette)))
					yield return r;
		}
	}
}
