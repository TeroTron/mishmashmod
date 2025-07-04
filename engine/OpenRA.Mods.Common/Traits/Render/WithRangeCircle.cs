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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	public enum RangeCircleVisibility { Always, WhenSelected }

	[Desc("Renders an arbitrary circle when selected or placing a structure")]
	sealed class WithRangeCircleInfo : ConditionalTraitInfo, IPlaceBuildingDecorationInfo
	{
		[Desc("Type of range circle. used to decide which circles to draw on other structures during building placement.")]
		public readonly string Type = null;

		[Desc("Color of the circle")]
		public readonly Color Color = Color.FromArgb(128, Color.White);

		[Desc("Border width.")]
		public readonly float Width = 1;

		[Desc("Color of the border.")]
		public readonly Color BorderColor = Color.FromArgb(96, Color.Black);

		[Desc("Range circle border width.")]
		public readonly float BorderWidth = 3;

		[Desc("If set, the color of the owning player will be used instead of `Color`.")]
		public readonly bool UsePlayerColor = false;

		[Desc("Player relationships which will be able to see the circle.",
			"Valid values are combinations of `None`, `Ally`, `Enemy` and `Neutral`.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Ally;

		[Desc("When to show the range circle. Valid values are `Always`, and `WhenSelected`")]
		public readonly RangeCircleVisibility Visible = RangeCircleVisibility.WhenSelected;

		[Desc("Range of the circle")]
		public readonly WDist Range = WDist.Zero;

		[Desc("Render the circle on the ground regardless of actors height.")]
		public readonly bool RenderOnGround = false;

		public IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World w, ActorInfo ai, WPos centerPosition)
		{
			var position = centerPosition - new WVec(WDist.Zero, WDist.Zero, RenderOnGround ? w.Map.DistanceAboveTerrain(centerPosition) : WDist.Zero);
			if (EnabledByDefault)
			{
				yield return new RangeCircleAnnotationRenderable(
					position,
					Range,
					0,
					Color,
					Width,
					BorderColor,
					BorderWidth);

				foreach (var a in w.ActorsWithTrait<WithRangeCircle>())
					if (a.Trait.Info.Type == Type)
						foreach (var r in a.Trait.RenderRangeCircle(a.Actor, RangeCircleVisibility.WhenSelected))
							yield return r;
			}
		}

		public override object Create(ActorInitializer init) { return new WithRangeCircle(init.Self, this); }
	}

	sealed class WithRangeCircle : ConditionalTrait<WithRangeCircleInfo>, IRenderAnnotationsWhenSelected, IRenderAnnotations
	{
		readonly Actor self;

		public WithRangeCircle(Actor self, WithRangeCircleInfo info)
			: base(info)
		{
			this.self = self;
		}

		bool Visible
		{
			get
			{
				if (IsTraitDisabled)
					return false;

				var p = self.World.RenderPlayer;
				return p == null || Info.ValidRelationships.HasRelationship(self.Owner.RelationshipWith(p)) || (p.Spectating && !p.NonCombatant);
			}
		}

		public IEnumerable<IRenderable> RenderRangeCircle(Actor self, RangeCircleVisibility visibility)
		{
			var position = self.CenterPosition - new WVec(WDist.Zero, WDist.Zero, Info.RenderOnGround
				? self.World.Map.DistanceAboveTerrain(self.CenterPosition)
				: WDist.Zero);
			if (Info.Visible == visibility && Visible)
				yield return new RangeCircleAnnotationRenderable(
					position,
					Info.Range,
					0,
					Info.UsePlayerColor ? self.OwnerColor() : Info.Color,
					Info.Width,
					Info.BorderColor,
					Info.BorderWidth);
		}

		IEnumerable<IRenderable> IRenderAnnotationsWhenSelected.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			return RenderRangeCircle(self, RangeCircleVisibility.WhenSelected);
		}

		bool IRenderAnnotationsWhenSelected.SpatiallyPartitionable => false;

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			return RenderRangeCircle(self, RangeCircleVisibility.Always);
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}
}
